#!/usr/bin/env bash
# Builds, packages and (with --publish) uploads a GitHub release that the in-app updater picks up.
#
#   scripts/release.sh 0.2.0              # build + package into dist/0.2.0
#   scripts/release.sh 0.2.0 --publish    # …and create the GitHub release (tag v0.2.0)
#   scripts/release.sh 0.2.0 --publish --draft
#
# Assets: Pickverter-<v>.dmg (people), Pickverter-<v>.zip (updater), update.json (signed manifest), SHA256SUMS.txt.
# Signing key for update.json: PICKVERTER_UPDATE_KEY (base64) or ~/.config/pickverter/update_ed25519.key.
# Optional: PICKVERTER_SIGN_IDENTITY (Developer ID) and PICKVERTER_NOTARY_PROFILE (xcrun notarytool keychain profile).
set -euo pipefail

VERSION="${1:?usage: scripts/release.sh <version> [--publish] [--draft]}"
VERSION="${VERSION#v}"
shift
PUBLISH=0
DRAFT=()
for arg in "$@"; do
  case "$arg" in
    --publish) PUBLISH=1 ;;
    --draft) DRAFT=(--draft) ;;
    *) echo "unknown option $arg" >&2; exit 64 ;;
  esac
done
if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
  echo "version must look like 1.2.3 or 1.2.3-beta.1" >&2
  exit 64
fi

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
REPO="$(/usr/libexec/PlistBuddy -c 'Print :PVUpdateRepository' Resources/Info.plist)"
TAG="v$VERSION"
BUILD="${PICKVERTER_BUILD:-$(git rev-list --count HEAD 2>/dev/null || echo 1)}"
OUT="$ROOT/dist/$VERSION"
if [ -e "$OUT" ]; then
  echo "$OUT already exists — move it away first" >&2
  exit 1
fi
mkdir -p "$OUT"

PICKVERTER_VERSION="$VERSION" PICKVERTER_BUILD="$BUILD" PICKVERTER_RELEASE=1 \
  PICKVERTER_ARCHS="${PICKVERTER_ARCHS:-arm64 x86_64}" scripts/build-app.sh release
APP="$ROOT/build/Pickverter.app"
CLI="$APP/Contents/MacOS/Pickverter"
codesign --verify --deep --strict "$APP"

ZIP="$OUT/Pickverter-$VERSION.zip"
DMG="$OUT/Pickverter-$VERSION.dmg"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"

if [ -n "${PICKVERTER_NOTARY_PROFILE:-}" ] && [ -n "${PICKVERTER_SIGN_IDENTITY:-}" ]; then
  echo "▸ notarizing"
  xcrun notarytool submit "$ZIP" --keychain-profile "$PICKVERTER_NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  mv "$ZIP" "$OUT/unstapled.zip.tmp"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
  mv "$OUT/unstapled.zip.tmp" "$ROOT/build/unstapled-$VERSION.zip"
fi

echo "▸ disk image"
STAGE="$(mktemp -d)"
ditto "$APP" "$STAGE/Pickverter.app"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "Pickverter $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null

NOTES="$OUT/notes.md"
awk -v v="$VERSION" '
  $0 ~ "^## \\[" v "\\]" { found = 1; next }
  found && /^## \[/ { exit }
  found { print }
' CHANGELOG.md | sed -e '/./,$!d' > "$NOTES"
[ -s "$NOTES" ] || echo "Pickverter $VERSION" > "$NOTES"

URL="https://github.com/$REPO/releases/download/$TAG/Pickverter-$VERSION.zip"
"$CLI" cli update-manifest --zip "$ZIP" --version "$VERSION" --build "$BUILD" --url "$URL" \
  --notes "$NOTES" --notes-url "https://github.com/$REPO/releases/tag/$TAG" > "$OUT/update.json"
"$CLI" cli update-verify "$OUT/update.json" "$ZIP"
(cd "$OUT" && shasum -a 256 "Pickverter-$VERSION.zip" "Pickverter-$VERSION.dmg" > SHA256SUMS.txt)

BODY="$OUT/release-body.md"
{
  cat "$NOTES"
  echo
  cat docs/release-footer.md
} > "$BODY"

echo "✓ $OUT"
ls -lh "$OUT"

if [ "$PUBLISH" = "1" ]; then
  PRERELEASE=()
  [[ "$VERSION" == *-* ]] && PRERELEASE=(--prerelease)
  gh release create "$TAG" "$DMG" "$ZIP" "$OUT/update.json" "$OUT/SHA256SUMS.txt" \
    --repo "$REPO" --title "Pickverter $VERSION" --notes-file "$BODY" "${DRAFT[@]}" "${PRERELEASE[@]}"
  echo "✓ published $TAG"
fi

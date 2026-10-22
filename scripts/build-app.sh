#!/usr/bin/env bash
# Builds build/Pickverter.app with swiftc directly (no SwiftPM needed).
# Usage: scripts/build-app.sh [release|debug] [--run]
#
# Optional environment (used by scripts/release.sh and CI):
#   PICKVERTER_VERSION=1.2.0      CFBundleShortVersionString in the built app
#   PICKVERTER_BUILD=42           CFBundleVersion
#   PICKVERTER_RELEASE=1          marks the app as a release build (enables self-updates)
#   PICKVERTER_ARCHS="arm64 x86_64"  universal binary (default: this Mac's architecture)
#   PICKVERTER_SIGN_IDENTITY="Developer ID Application: …"  real signature + hardened runtime (default: ad-hoc)
#   PICKVERTER_SDK=/path/to/MacOSX.sdk
set -euo pipefail

CONFIG="${1:-release}"
RUN_AFTER="${2:-}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
mkdir -p build

# Pick an SDK. Newer SDKs implement SwiftUI's @State as a macro; when the toolchain ships without the
# SwiftUIMacros plugin (Command Line Tools only) we fall back to the newest SDK that doesn't need it.
SDK="${PICKVERTER_SDK:-$(xcrun --show-sdk-path)}"
if [ -z "${PICKVERTER_SDK:-}" ]; then
  TOOLCHAIN_DIR="$(dirname "$(dirname "$(xcrun -f swiftc)")")"
  DEVELOPER_DIR_PATH="$(xcode-select -p 2>/dev/null || true)"
  if ! find "$TOOLCHAIN_DIR/lib/swift/host/plugins" "$DEVELOPER_DIR_PATH/Platforms/MacOSX.platform/Developer/usr/lib/swift/host/plugins" \
       -iname '*SwiftUIMacros*' 2>/dev/null | grep -q .; then
    SDK_ROOT="$(dirname "$SDK")"
    for candidate in MacOSX26.sdk MacOSX15.sdk MacOSX14.sdk; do
      if [ -d "$SDK_ROOT/$candidate" ]; then SDK="$SDK_ROOT/$candidate"; break; fi
    done
  fi
fi

if [ "$CONFIG" = "debug" ]; then OPT=(-Onone -g); else OPT=(-O -whole-module-optimization); fi
ARCHS="${PICKVERTER_ARCHS:-$(uname -m)}"

SOURCES=()
while IFS= read -r file; do SOURCES+=("$file"); done < <(find Sources -name '*.swift' | sort)

SLICES=()
for ARCH in $ARCHS; do
  echo "▸ swiftc ($CONFIG, $ARCH) · SDK $(basename "$SDK")"
  swiftc "${OPT[@]}" -swift-version 5 -sdk "$SDK" -target "$ARCH-apple-macosx14.0" -parse-as-library \
    -module-name Pickverter "${SOURCES[@]}" -o "build/Pickverter-$ARCH"
  SLICES+=("build/Pickverter-$ARCH")
done
if [ "${#SLICES[@]}" -gt 1 ]; then
  lipo -create "${SLICES[@]}" -output build/Pickverter
else
  cp "${SLICES[0]}" build/Pickverter
fi

APP="$ROOT/build/Pickverter.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp build/Pickverter "$APP/Contents/MacOS/Pickverter"
cp Resources/Info.plist "$APP/Contents/Info.plist"
PLIST="$APP/Contents/Info.plist"
if [ -n "${PICKVERTER_VERSION:-}" ]; then /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $PICKVERTER_VERSION" "$PLIST"; fi
if [ -n "${PICKVERTER_BUILD:-}" ]; then /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $PICKVERTER_BUILD" "$PLIST"; fi
/usr/libexec/PlistBuddy -c "Delete :PVReleaseBuild" "$PLIST" >/dev/null 2>&1 || true
if [ "${PICKVERTER_RELEASE:-}" = "1" ]; then /usr/libexec/PlistBuddy -c "Add :PVReleaseBuild bool true" "$PLIST"; fi
cp Resources/i18n.json "$APP/Contents/Resources/i18n.json"
# Per-language folders make system-provided UI (panels, permission prompts) localized as well.
# Helpers run from the loose binary: the bundle isn't signed yet, and macOS kills an executable whose
# bundle seal no longer matches (e.g. after Info.plist changed).
build/Pickverter cli i18n-plist "$APP/Contents/Resources" >/dev/null

ICNS="$ROOT/build/AppIcon.icns"
if [ ! -f "$ICNS" ] || [ Sources/Pickverter/UI/BrandArt.swift -nt "$ICNS" ]; then
  echo "▸ rendering app icon"
  ICONSET="$ROOT/build/AppIcon.iconset"
  mkdir -p "$ICONSET"
  build/Pickverter cli icon "$ICONSET" >/dev/null
  iconutil -c icns "$ICONSET" -o "$ICNS"
fi
cp "$ICNS" "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc by default. With a real identity: hardened runtime + entitlements (needed for notarization).
IDENTITY="${PICKVERTER_SIGN_IDENTITY:--}"
if [ "$IDENTITY" = "-" ]; then
  codesign --force --sign - --identifier com.pickverter.app "$APP" >/dev/null 2>&1
else
  codesign --force --sign "$IDENTITY" --identifier com.pickverter.app --options runtime --timestamp \
    --entitlements Resources/Pickverter.entitlements "$APP"
fi

echo "✓ $APP ($(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$PLIST"), $ARCHS)"

if [ "$RUN_AFTER" = "--run" ]; then
  pkill -x Pickverter 2>/dev/null || true
  sleep 0.3
  open "$APP"
fi

#!/usr/bin/env bash
# End-to-end checks: translations, wheel self-test, SF Symbols, then every engine on generated fixtures.
# Editors are answered through PICKVERTER_EDITOR_* so nothing appears on screen.
#
#   scripts/test.sh            # all cases (media cases that need ffmpeg run when it is installed)
#   scripts/test.sh --quick    # skip ffmpeg-only cases
#   KEEP=1 scripts/test.sh     # keep the scratch folder for inspection
# HWP cases run when Pickverter's rhwp is installed (cli rhwp-install; PICKVERTER_RHWP_DIR picks the folder).
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
QUICK=0
[ "${1:-}" = "--quick" ] && QUICK=1
APP="$ROOT/build/Pickverter.app"
CLI="$APP/Contents/MacOS/Pickverter"
[ -x "$CLI" ] || scripts/build-app.sh debug

WORK="$(mktemp -d "${TMPDIR:-/tmp}/pickverter-test.XXXXXX")"
FIX="$WORK/fixtures"
PASS=0
FAIL=0
SKIP=0
FAILED=()

green() { printf '\033[32m%s\033[0m\n' "$*"; }
red() { printf '\033[31m%s\033[0m\n' "$*"; }

step() {
  local name="$1"; shift
  if "$@" >"$WORK/$name.log" 2>&1; then
    PASS=$((PASS + 1)); green "PASS $name"
  else
    FAIL=$((FAIL + 1)); FAILED+=("$name"); red "FAIL $name"; sed 's/^/     /' "$WORK/$name.log" | tail -15
  fi
}

step i18n python3 scripts/check-i18n.py
step symbols "$CLI" cli symbols
"$CLI" cli fixtures "$FIX" >/dev/null || { red "fixtures failed"; exit 1; }
step selftest "$CLI" cli selftest "$FIX/photo.jpg"

HAS_FFMPEG=0
if [ "$QUICK" = 0 ] && command -v ffmpeg >/dev/null 2>&1; then
  HAS_FFMPEG=1
  ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=duration=6:size=640x360:rate=30 \
    -f lavfi -i sine=frequency=330:duration=6 -shortest -c:v libx264 -pix_fmt yuv420p -c:a aac "$FIX/talk.mp4"
  ffmpeg -hide_banner -loglevel error -i "$FIX/tone.wav" -c:a libmp3lame -q:a 4 "$FIX/tone.mp3"
  # Film grain doesn't compress: about 30 MB for six seconds, so "Under 10 MB" has real work to do.
  ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=duration=6:size=1280x720:rate=30 \
    -f lavfi -i sine=frequency=220:duration=6 -shortest -vf noise=alls=25:allf=t -c:v libx264 -crf 16 \
    -pix_fmt yuv420p -c:a aac "$FIX/grain.mp4"
fi

HAS_RHWP=0
RHWP_DIR="${PICKVERTER_RHWP_DIR:-$HOME/Library/Application Support/Pickverter/rhwp}"
RHWP_VERSION="$(sed -n 's/.*static let version = "\(.*\)".*/\1/p' Sources/Pickverter/Engines/HWPInstaller.swift)"
if [ -x "$RHWP_DIR/rhwp" ] && [ "$(cat "$RHWP_DIR/version.txt" 2>/dev/null)" = "$RHWP_VERSION" ]; then
  HAS_RHWP=1
  export PICKVERTER_RHWP_DIR="$RHWP_DIR"
  "$RHWP_DIR/rhwp" convert "$FIX/report.hwpx" "$FIX/report.hwp" >/dev/null 2>&1 || true
  head -c 4096 /dev/urandom > "$FIX/broken.hwp"
fi

# t <name> <check> <option-id> <fixture…>   (optional: E='VAR=value' for the editor answer)
# checks: ext:<ext> · dir:<count> · msg · fact:<key>=<value> · near:<key>=<value> · epub · clip · fail
#         text:<words> (in the UTF-8 result) · has:<name> (stored composed in the result folder) · max:<bytes>
#         bom (UTF-8 byte-order mark) · zipnames (composed, flagged UTF-8, no Mac clutter)
t() {
  local name="$1" check="$2" option="$3"; shift 3
  local dir="$WORK/$name"
  for fixture in "$@"; do
    if [ ! -e "$FIX/$fixture" ]; then SKIP=$((SKIP + 1)); echo "SKIP $name (no $fixture on this machine)"; return; fi
  done
  mkdir -p "$dir"
  local files=()
  for fixture in "$@"; do cp -R "$FIX/$fixture" "$dir/"; files+=("$dir/$fixture"); done
  local log="$dir/run.log"
  local env_args=()
  [ -n "${E:-}" ] && env_args+=("$E")
  [ -n "${DEST:-}" ] && env_args+=("PICKVERTER_DEST=$DEST")
  env "${env_args[@]+"${env_args[@]}"}" "$CLI" cli run "$option" "${files[@]}" >"$log" 2>&1
  local status=$?
  if [ "$status" = 3 ]; then SKIP=$((SKIP + 1)); echo "SKIP $name ($(head -1 "$log"))"; return; fi
  local out
  out="$(grep '^OUT ' "$log" | head -1 | sed -E 's/^OUT (.*) \([^)]*\)$/\1/')"
  local ok=1 why=""
  if [ "$check" = fail ]; then
    [ "$status" != 0 ] || { ok=0; why="expected a failure"; }
  elif [ "$status" != 0 ]; then
    ok=0; why="exit $status"
  else
    case "$check" in
      ext:*) [[ -e "$out" && "$out" == *."${check#ext:}" ]] || { ok=0; why="no .${check#ext:} output"; } ;;
      dir:*) local n; n=$(find "$out" -maxdepth 1 -type f ! -name '.*' 2>/dev/null | wc -l | tr -d ' ')
             [ "$n" = "${check#dir:}" ] || { ok=0; why="folder has $n files"; } ;;
      msg) grep -q '^MSG ' "$log" || { ok=0; why="no message"; } ;;
      clip) grep -q '^CLIP ' "$log" || { ok=0; why="nothing on the clipboard"; } ;;
      fact:*) local want="${check#fact:}"
              "$CLI" cli inspect "$out" | grep -qx "$want" || { ok=0; why="$(tr '\n' ' ' < <("$CLI" cli inspect "$out")) ≠ $want"; } ;;
      near:*) local key="${check#near:}"; key="${key%%=*}"; local want="${check#*=}"
              local got; got="$("$CLI" cli inspect "$out" | sed -n "s/^$key=//p")"
              python3 -c "import sys; sys.exit(0 if abs(float('$got' or 0) - $want) <= 0.35 else 1)" \
                || { ok=0; why="$key=$got (want ≈ $want)"; } ;;
      epub) if ! unzip -Z1 "$out" | head -1 | grep -qx mimetype; then ok=0; why="mimetype not first"
            else
              local x="$dir/epub"; mkdir -p "$x"; unzip -q "$out" -d "$x"
              for f in "$x"/OEBPS/*.xhtml "$x"/OEBPS/text/*.xhtml "$x"/OEBPS/content.opf "$x"/META-INF/container.xml; do
                xmllint --noout "$f" 2>>"$log" || { ok=0; why="invalid XML in ${f#$x/}"; }
              done
            fi ;;
      text:*) python3 -c 'import sys; sys.exit(0 if sys.argv[2] in open(sys.argv[1], "rb").read().decode("utf-8") else 1)' \
                "$out" "${check#text:}" 2>>"$log" || { ok=0; why="no “${check#text:}” in UTF-8"; } ;;
      has:*) python3 -c 'import os, sys; sys.exit(0 if sys.argv[2].encode() in os.listdir(sys.argv[1].encode()) else 1)' \
               "$out" "${check#has:}" || { ok=0; why="no composed “${check#has:}” in $(ls "$out" 2>/dev/null | tr '\n' ' ')"; } ;;
      max:*) local bytes; bytes=$(stat -f %z "$out")
             [ "$bytes" -le "${check#max:}" ] || { ok=0; why="$bytes bytes > ${check#max:}"; } ;;
      bom) [ "$(head -c 3 "$out" | xxd -p)" = "efbbbf" ] || { ok=0; why="no byte-order mark"; } ;;
      zipnames) python3 - "$out" <<'PY' 2>>"$log" || { ok=0; why="names not composed UTF-8 or clutter inside"; }
import sys, unicodedata, zipfile
entries = zipfile.ZipFile(sys.argv[1]).infolist()
names = [e.filename for e in entries]
assert names, "empty"
assert all(unicodedata.is_normalized("NFC", n) for n in names), names
assert all(e.flag_bits & 0x800 for e in entries if not e.filename.isascii()), names
assert not any(".DS_Store" in n or "__MACOSX" in n or "/._" in n for n in names), names
PY
             ;;
    esac
  fi
  if [ "$ok" = 1 ]; then
    PASS=$((PASS + 1)); green "PASS $name"
  else
    FAIL=$((FAIL + 1)); FAILED+=("$name"); red "FAIL $name — $why"; sed 's/^/     /' "$log" | tail -6
  fi
}

echo "— images"
t img-png ext:png convert.png photo.jpg
t img-heic ext:heic convert.heic photo.jpg
t img-resize fact:size=800x500 tool.resize.50 photo.jpg
t img-crop-square fact:size=1000x1000 tool.crop.square photo.jpg
E='PICKVERTER_EDITOR_CROP={"rect":{"x":0.25,"y":0.25,"w":0.5,"h":0.5}}' t img-crop-editor fact:size=800x500 tool.crop.custom photo.jpg
E='PICKVERTER_EDITOR_REDACT={"regions":[{"x":0.1,"y":0.1,"w":0.3,"h":0.3}],"style":"pixelate"}' t img-redact fact:gps=no tool.redact.custom photo.jpg
E='PICKVERTER_EDITOR_REDACT={"regions":[{"x":0.5,"y":0.5,"w":0.2,"h":0.2}],"style":"blur"}' t img-blur ext:jpg tool.redact.custom photo.jpg
t img-redact-text ext:png tool.redact.text sign.png
t img-strip-gps fact:gps=no tool.meta.gps photo.jpg
E='PICKVERTER_EDITOR_METADATA={"fields":{"title":"Hello Pickverter"},"removeLocation":false,"replaceOriginal":false}' t img-meta fact:title='Hello Pickverter' tool.meta.edit photo.jpg
t img-rotate fact:size=1000x1600 tool.rotate.left photo.jpg
t img-ocr msg tool.ocr sign.png
t img-collage ext:jpg tool.collage.grid photo.jpg sign.png
t img-strip fact:size=3200x1000 tool.collage.row photo.jpg photo.jpg
t img-book epub tool.picturebook photo.jpg logo.png
DEST=clipboard t img-clipboard clip convert.png photo.jpg

echo "— PDF"
t pdf-blank fact:pages=2 tool.pages.blank report.pdf
t pdf-reverse fact:pages=3 tool.pages.reverse report.pdf
t pdf-odd fact:pages=2 tool.pages.odd report.pdf
E='PICKVERTER_EDITOR_RANGE=3, 1' t pdf-range fact:pages=2 tool.pages.range report.pdf
E='PICKVERTER_EDITOR_RANGE=9-12' t pdf-range-bad fail tool.pages.range report.pdf
E='PICKVERTER_EDITOR_PAGES={"pages":[{"file":0,"page":2,"rotation":90},{"file":0,"page":0,"rotation":0}]}' t pdf-organize fact:rotation=90 tool.pages.organize report.pdf
E='PICKVERTER_EDITOR_METADATA={"fields":{"title":"Quarterly"},"removeLocation":false,"replaceOriginal":false}' t pdf-meta fact:title=Quarterly tool.meta report.pdf
t pdf-images dir:3 convert.png.150 report.pdf
t pdf-split dir:3 tool.split report.pdf

echo "— documents & office"
t doc-epub epub convert.epub book.md
t doc-epub-many epub tool.epub book.md notes.txt
t doc-pdf ext:pdf convert.pdf book.md
t doc-html ext:html convert.html notes.txt
t xlsx-csv dir:2 convert.csv sheet.xlsx
t xlsx-json ext:json convert.json sheet.xlsx
t xlsx-md ext:md convert.md sheet.xlsx
t pptx-md ext:md convert.md deck.pptx
t pptx-media dir:1 tool.media deck.pptx
t pptx-text msg tool.copytext deck.pptx
t csv-json ext:json convert.json people.csv

echo "— Mac ↔ Windows"
t txt-utf8 text:똠방각하 tool.utf8 notes-cp949.txt
t smi-srt text:자막 convert.srt movie.smi
t smi-vtt text:반갑습니다 convert.vtt movie.smi
t zip-from-windows has:결과.txt tool.extract windows.zip
t zip-from-mac has:메모.txt tool.extract mac.zip
t zip-for-windows zipnames tool.zip names
t names-join msg action.nfc names
t csv-bom bom convert.csv people.json
t img-under-500k max:500000 tool.compress.max500k noise.jpg
E='PICKVERTER_EDITOR_SIZE=1.2' t img-under-custom max:1200000 tool.compress.maxcustom noise.jpg
t img-already-under msg tool.compress.max5m photo.jpg

if [ "$HAS_RHWP" = 1 ]; then
  echo "— HWP (rhwp $RHWP_VERSION)"
  t hwpx-pdf fact:pages=1 convert.pdf report.hwpx
  t hwpx-hwp fact:format=hwp5 convert.hwp report.hwpx
  t hwp-hwpx fact:format=hwpx convert.hwpx report.hwp
  t hwpx-docx ext:docx convert.docx report.hwpx
  t hwp-txt ext:txt convert.txt report.hwp
  t hwpx-md ext:md convert.md report.hwpx
  t hwpx-html ext:html convert.html report.hwpx
  t hwpx-png ext:png convert.png.150 report.hwpx
  t hwp-text msg tool.copytext report.hwp
  t hwpx-count msg tool.count report.hwpx
  t hwp-broken fail convert.pdf broken.hwp
fi

echo "— audio & video (AVFoundation)"
E='PICKVERTER_EDITOR_TRIM={"start":1,"end":2.5,"lossless":false}' t wav-cut near:duration=1.5 tool.cut tone.wav
t wav-split dir:2 tool.parts.2 tone.wav
E='PICKVERTER_EDITOR_TRIM={"start":0.5,"end":2,"lossless":false}' t mov-cut near:duration=1.5 tool.cut clip.mov
t mov-snapshot ext:png tool.snapshot.middle clip.mov
t mov-split dir:3 tool.parts.3 clip.mov

if [ "$HAS_FFMPEG" = 1 ]; then
  echo "— audio & video (ffmpeg)"
  E='PICKVERTER_EDITOR_CROP={"rect":{"x":0,"y":0,"w":0.5,"h":0.5}}' t mp4-crop fact:size=320x180 tool.crop.custom talk.mp4
  t mp4-crop-square fact:size=360x360 tool.crop.square talk.mp4
  E='PICKVERTER_EDITOR_REDACT={"regions":[{"x":0.1,"y":0.1,"w":0.3,"h":0.3}],"style":"pixelate"}' t mp4-redact near:duration=6 tool.redact talk.mp4
  E='PICKVERTER_EDITOR_REDACT={"regions":[{"x":0.4,"y":0.4,"w":0.2,"h":0.2}],"style":"blur"}' t mp4-blur near:duration=6 tool.redact talk.mp4
  E='PICKVERTER_EDITOR_TRIM={"start":1,"end":3,"lossless":false}' t mp4-cut near:duration=2 tool.cut talk.mp4
  E='PICKVERTER_EDITOR_TRIM={"start":1,"end":3,"lossless":true}' t mp4-cut-fast ext:mp4 tool.cut talk.mp4
  t mp4-join near:duration=12 tool.join talk.mp4 talk.mp4
  t mp4-join-mixed near:duration=9 tool.join talk.mp4 clip.mov
  t mp4-split dir:2 tool.parts.2 talk.mp4
  t mp4-gif ext:gif convert.gif.480 talk.mp4
  E='PICKVERTER_EDITOR_METADATA={"fields":{"title":"Demo clip"},"removeLocation":false,"replaceOriginal":false}' t mp4-meta fact:title='Demo clip' tool.meta talk.mp4
  t wav-silence near:duration=3 tool.silence tone.wav
  t mp3-viz ext:mp4 tool.visualize.wave tone.mp3
  t mp3-join near:duration=8 tool.join tone.mp3 tone.mp3
  t mp4-under-10m max:10000000 tool.compress.max10m grain.mp4
fi

echo
echo "passed $PASS · failed $FAIL · skipped $SKIP"
if [ "${KEEP:-0}" = 1 ]; then echo "scratch: $WORK"; else rm -rf "$WORK"; fi
[ "$FAIL" = 0 ] || { printf 'failed: %s\n' "${FAILED[*]}"; exit 1; }

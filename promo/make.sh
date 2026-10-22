#!/bin/bash
# Builds the promo video: events → sound → 4K frames (motion blur) → mux → 1080p copy.
#   promo/make.sh [en|ko|all]   (default: all)
set -euo pipefail
cd "$(dirname "$0")"

LANGS="${1:-all}"
[ "$LANGS" = "all" ] && LANGS="en ko"
SAMPLES="${PROMO_SAMPLES:-4}"
WORKERS="${PROMO_WORKERS:-6}"

[ -d node_modules ] || npm install --silent
[ -x .venv/bin/python ] || { python3 -m venv .venv && .venv/bin/pip install -q numpy scipy pyloudnorm; }
[ -d assets/symbols ] || swift tools/export-assets.swift assets
mkdir -p out

for L in $LANGS; do
  echo "▸ $L: sound"
  node render.mjs --lang "$L" --events "out/events-$L.json"
  .venv/bin/python audio/synth.py "out/events-$L.json" "out/audio-$L.wav"

  echo "▸ $L: 4K frames ($SAMPLES samples per frame)"
  node render.mjs --lang "$L" --video "out/video-$L-4k.mp4" --dpr 2 --samples "$SAMPLES" --workers "$WORKERS"

  echo "▸ $L: mux"
  ffmpeg -v error -y -i "out/video-$L-4k.mp4" -i "out/audio-$L.wav" -map 0:v -map 1:a -c:v copy \
    -c:a aac -b:a 256k -ar 48000 -shortest -movflags +faststart "out/Pickverter-promo-$L-4k.mp4"
  ffmpeg -v error -y -i "out/Pickverter-promo-$L-4k.mp4" -vf "scale=1920:1080:flags=lanczos" \
    -c:v libx264 -preset slow -crf 17 -pix_fmt yuv420p -color_primaries bt709 -color_trc bt709 -colorspace bt709 \
    -c:a copy -movflags +faststart "out/Pickverter-promo-$L-1080p.mp4"
  echo "✓ out/Pickverter-promo-$L-4k.mp4"
  echo "✓ out/Pickverter-promo-$L-1080p.mp4"
done


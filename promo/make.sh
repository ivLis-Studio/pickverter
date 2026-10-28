#!/bin/bash
# Builds the promo video: events → sound → frames (4K-class, motion blur) → mux → delivery sizes.
#   promo/make.sh [en|ko|all]          (default: all)
#   PROMO_ASPECTS="h v"                16:9 and 9:16 (default both)
#   PROMO_PLATFORMS="mac win"          the Mac cut, the Windows cut or both (default mac)
#   PROMO_STORE=1                      with PROMO_PLATFORMS=win: the Windows cut's Microsoft Store version, without the
#                                      phone (Store policy 10.1.1.3), for promo/store.sh
# Outputs per language in out/:
#   Pickverter-promo-<lang>-4k.mp4 (3840×2160) · -1080p.mp4 (1920×1080)
#   Pickverter-promo-<lang>-vertical-4k.mp4 (2160×3840) · -vertical.mp4 (1080×1920, Reels / Shorts / TikTok)
#   The Windows cut is named Pickverter-promo-windows-<lang>-….mp4, its Store version Pickverter-promo-windows-store-<lang>-….mp4
set -euo pipefail
cd "$(dirname "$0")"

LANGS="${1:-all}"
[ "$LANGS" = "all" ] && LANGS="en ko"
ASPECTS="${PROMO_ASPECTS:-h v}"
PLATFORMS="${PROMO_PLATFORMS:-mac}"
STORE="${PROMO_STORE:-}"
SAMPLES="${PROMO_SAMPLES:-4}"
WORKERS="${PROMO_WORKERS:-6}"

[ -d node_modules ] || npm install --silent
[ -x .venv/bin/python ] || { python3 -m venv .venv && .venv/bin/pip install -q numpy scipy pyloudnorm; }
case " $PLATFORMS " in *" mac "*) [ -d assets/symbols ] || swift tools/export-assets.swift assets ;; esac
mkdir -p out

for P in $PLATFORMS; do
  if [ "$P" = "win" ]; then TAG="windows-"; PRE="win-"; else TAG=""; PRE=""; fi
  FLAGS=""
  if [ -n "$STORE" ]; then
    [ "$P" = "win" ] || { echo "make.sh: PROMO_STORE is a version of the Windows cut (PROMO_PLATFORMS=win)" >&2; exit 2; }
    TAG="windows-store-"; PRE="win-store-"; FLAGS="--store"
  fi
  for L in $LANGS; do
    echo "▸ $P $L: sound"
    node render.mjs --platform "$P" --lang "$L" $FLAGS --events "out/events-$PRE$L.json"
    .venv/bin/python audio/synth.py "out/events-$PRE$L.json" "out/audio-$PRE$L.wav"

    for A in $ASPECTS; do
      if [ "$A" = "v" ]; then BIG="vertical-4k"; SMALL="vertical"; SIZE="1080:1920"; else BIG="4k"; SMALL="1080p"; SIZE="1920:1080"; fi
      echo "▸ $P $L $A: frames ($SAMPLES samples per frame)"
      node render.mjs --platform "$P" --lang "$L" --aspect "$A" $FLAGS --video "out/video-$PRE$L-$A.mp4" --dpr 2 --samples "$SAMPLES" --workers "$WORKERS"

      echo "▸ $P $L $A: mux"
      ffmpeg -v error -y -i "out/video-$PRE$L-$A.mp4" -i "out/audio-$PRE$L.wav" -map 0:v -map 1:a -c:v copy \
        -c:a aac -b:a 256k -ar 48000 -shortest -movflags +faststart "out/Pickverter-promo-$TAG$L-$BIG.mp4"
      ffmpeg -v error -y -i "out/Pickverter-promo-$TAG$L-$BIG.mp4" -vf "scale=$SIZE:flags=lanczos" \
        -c:v libx264 -preset slow -crf 17 -profile:v high -pix_fmt yuv420p -color_primaries bt709 -color_trc bt709 -colorspace bt709 \
        -c:a copy -movflags +faststart "out/Pickverter-promo-$TAG$L-$SMALL.mp4"
      echo "✓ out/Pickverter-promo-$TAG$L-$BIG.mp4"
      echo "✓ out/Pickverter-promo-$TAG$L-$SMALL.mp4"
    done
  done
done

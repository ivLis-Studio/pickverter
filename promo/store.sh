#!/bin/bash
# Microsoft Store listing assets for Pickverter for Windows, made from the Store version of the film's Windows cut: the
# same film, but the share ends in Phone Link's Windows notification instead of on an Android phone. Store policy
# 10.1.1.3 wants listing images to show Windows UI and devices only (the 0.3.3 submission was turned down for the phone).
#   promo/store.sh                  screenshots, art and trailers
#   promo/store.sh art trailer      only some of them (shots · art · trailer)
#   STORE_LANGS=en promo/store.sh   one language (default: en ko)
# Outputs in out/store/:
#   screenshots/<lang>/01-…06-….png   Desktop screenshots, 3840×2160, in listing order
#   art/                              StoreLogo 300×300 · box art 1:1 · poster art 2:3 · super hero art 16:9 (store.html)
#   trailer/                          Pickverter-trailer-<lang>.mp4 and its 1920×1080 PNG thumbnail
# The trailers are encoded from the Store version's 4K masters and WAV scores, so render those first:
#   PROMO_STORE=1 PROMO_PLATFORMS=win PROMO_ASPECTS=h promo/make.sh
set -euo pipefail
cd "$(dirname "$0")"

PARTS="${*:-shots art trailer}"
LANGS="${STORE_LANGS:-en ko}"
OUT=out/store
# Seconds into the film : file name. Each moment is held still (nothing mid-motion), captions included. 06 is the share
# sheet with the pointer on Phone Link.
SHOTS="3.0:01-webp-to-jpg 7.8:02-word-to-pdf 12.5:03-rotate 15.4:04-remove-background 17.8:05-paste-cutout 21.0:06-share-to-phone"
THUMB=1.05 # the hook, "Every file. One key away." over the Alt key; the middle stays clear for the Store's play button

[ -d node_modules ] || npm install --silent
# Store images are opaque: drop the alpha channel the browser screenshots carry.
flat() { magick "$1" -alpha off -define png:color-type=2 "$2"; }
still() { echo "out/stills/win-store-$1-$(printf '%.2f' "$2").png"; }

for part in $PARTS; do
  case $part in
    shots)
      for L in $LANGS; do
        times=""
        for s in $SHOTS; do times="$times,${s%%:*}"; done
        node render.mjs --platform win --store --lang "$L" --dpr 2 --stills "${times#,}" >/dev/null
        mkdir -p "$OUT/screenshots/$L"
        for s in $SHOTS; do flat "$(still "$L" "${s%%:*}")" "$OUT/screenshots/$L/${s#*:}.png"; done
        echo "✓ $OUT/screenshots/$L"
      done
      ;;
    art)
      mkdir -p "$OUT/art"
      shot() {
        node render.mjs --platform win --dpr "$4" --shot "store.html?art=$1" --width "$2" --height "$3" --out "$OUT/art/$5" >/dev/null
        flat "$OUT/art/$5" "$OUT/art/$5"
        echo "✓ $OUT/art/$5"
      }
      shot hero 1920 1080 1 SuperHeroArt-1920x1080.png
      shot hero 1920 1080 2 SuperHeroArt-3840x2160.png
      shot box 1080 1080 1 BoxArt-1080x1080.png
      shot box 1080 1080 2 BoxArt-2160x2160.png
      shot poster 720 1080 1 PosterArt-720x1080.png
      shot poster 720 1080 2 PosterArt-1440x2160.png
      # The app tile icon is the Windows app icon itself (windows/scripts/make-icons.mjs), transparent corners included.
      magick ../windows/docs/icon-1024.png -filter Lanczos -resize 300x300 "$OUT/art/StoreLogo-300x300.png"
      echo "✓ $OUT/art/StoreLogo-300x300.png"
      ;;
    trailer)
      mkdir -p "$OUT/trailer"
      log=$(mktemp -d)
      for L in $LANGS; do
        # Microsoft's trailer recommendations: H.264 High, progressive 4:2:0, two consecutive B-frames, a closed GOP of half
        # the frame rate (60 fps → 30 frames), CABAC, at most 50 Mbps (two passes at 14); AAC-LC, 48 kHz stereo, 384 kbps;
        # no metadata, no edit lists, the index up front. The 4K master and the WAV score save one generation of loss.
        # setparams tags the frames BT.709 (this ffmpeg drops -color_primaries/-color_trc on filtered frames otherwise).
        V=(-vf "scale=1920:1080:flags=lanczos,setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv"
          -c:v libx264 -preset slow -profile:v high -level:v 4.2 -pix_fmt yuv420p
          -b:v 14M -maxrate 16M -bufsize 32M -flags +cgop -passlogfile "$log/$L"
          -x264-params "bframes=2:b-adapt=0:b-pyramid=none:keyint=30:min-keyint=30:scenecut=0:open-gop=0:cabac=1"
          -color_primaries bt709 -color_trc bt709 -colorspace bt709)
        ffmpeg -v error -y -i "out/Pickverter-promo-windows-store-$L-4k.mp4" -map 0:v:0 "${V[@]}" -pass 1 -an -f null /dev/null
        ffmpeg -v error -y -i "out/Pickverter-promo-windows-store-$L-4k.mp4" -i "out/audio-win-store-$L.wav" -map 0:v:0 -map 1:a:0 "${V[@]}" -pass 2 \
          -c:a aac -profile:a aac_low -b:a 384k -ar 48000 -ac 2 -shortest \
          -map_metadata -1 -map_chapters -1 -use_editlist 0 -movflags +faststart "$OUT/trailer/Pickverter-trailer-$L.mp4"
        node render.mjs --platform win --store --lang "$L" --dpr 2 --stills "$THUMB" >/dev/null
        magick "$(still "$L" "$THUMB")" -filter Lanczos -resize 1920x1080 -alpha off -define png:color-type=2 "$OUT/trailer/Pickverter-trailer-$L-thumbnail.png"
        echo "✓ $OUT/trailer/Pickverter-trailer-$L.mp4 (+ thumbnail)"
      done
      ;;
    *) echo "store.sh: unknown part '$part' (shots, art or trailer)" >&2; exit 2 ;;
  esac
done

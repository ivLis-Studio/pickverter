# Pickverter promo video

A 35-second launch video rendered entirely from code — the stage, the motion and the sound.

```bash
promo/make.sh          # English and Korean
promo/make.sh ko       # one language
```

Outputs land in `promo/out/`: `Pickverter-promo-<lang>-4k.mp4` (3840×2160, 60 fps) and a 1080p copy.

How it works:

- `index.html` + `src/` draw the stage. `renderAt(t)` is a pure function of time, so every frame is reproducible.
  The wheel, result cards and keycaps follow the app's own geometry (`macos/Sources/Pickverter/Wheel/WheelView.swift`)
  and strings (`macos/Resources/i18n.json`); the finale morphs the wheel into the app icon drawn by `src/icon.js`
  (kept in sync with `AppIconArt` in `macos/Sources/Pickverter/UI/BrandArt.swift`).
- `render.mjs` drives headless Chrome, averages sub-frames for motion blur, and encodes chunks in parallel.
- `audio/synth.py` writes the score and every sound effect from scratch (no samples), timed from the events the stage records.
- `banner.html` draws the README banners and the social card:
  `node render.mjs --lang en --dpr 2 --shot "banner.html?theme=dark" --out out/readme/banner-en-dark.png`.

Where the outputs are used: the README banners and walkthrough images live in `docs/readme/`, and the film, poster and
social card on the website live in `website/public/assets/media/` — copy new renders there.

Requirements: macOS with Google Chrome, Node, Python 3, ffmpeg. SF Symbols and file icons are exported from the
local system by `tools/export-assets.swift`; the photos were generated for this video and the cut-out was made with
Pickverter's own Remove BG.


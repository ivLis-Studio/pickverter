# Pickverter promo video

A 31-second launch video rendered entirely from code — the stage, the motion and the sound — in 16:9 and 9:16.

```bash
promo/make.sh                         # English and Korean, 16:9 and 9:16
promo/make.sh ko                      # one language
PROMO_ASPECTS=v promo/make.sh ko      # only the vertical cut
```

Outputs land in `promo/out/`, all 60 fps:

- `Pickverter-promo-<lang>-4k.mp4` (3840×2160) and `-1080p.mp4`
- `Pickverter-promo-<lang>-vertical-4k.mp4` (2160×3840) and `-vertical.mp4` (1080×1920) for Reels, Shorts and TikTok.
  The vertical cut keeps captions and keys inside the platforms' safe area (nothing important in the bottom 400 px).

The edit runs on one beat grid (128 BPM, `window.MUSIC`) so energy only climbs until the icon:

| Bars | Scene |
| --- | --- |
| 0 | Hook — "Every file. One key away." and the ⌥ key |
| 1–2 | Three iPhone photos → JPG at once (marquee, the wheel blooms open) |
| 3–4 | Word → PDF |
| 5–6 | Rotate: hover a tool and its options fan out |
| 7–8 | Remove the background → clipboard → paste into a chat |
| 9–10½ | AirDrop the PDF to an iPhone |
| 10½–13 | Montage on blue: trim video, merge PDFs, blur faces, lock PDFs, shrink files, grab text |
| 14–16 | Fly out of the picked slot into the app icon, name, "by ivLis STUDIO", last hit |

How it works:

- `index.html` + `src/` draw the stage. `renderAt(t)` is a pure function of time, so every frame is reproducible.
  The wheel, result cards and keycaps follow the app's own geometry (`macos/Sources/Pickverter/Wheel/WheelView.swift`)
  and strings (`macos/Resources/i18n.json`); the finale zooms out of the icon drawn by `src/icon.js`
  (kept in sync with `AppIconArt` in `macos/Sources/Pickverter/UI/BrandArt.swift`). `src/scene.js` holds the timeline,
  the camera and both layouts; `src/montage.js` the feature ring and its little animations.
- `render.mjs` drives headless Chrome, averages sub-frames for motion blur, and encodes chunks in parallel.
- `audio/synth.py` writes the score (128 BPM, A major, four-on-the-floor with sidechain pump) and every sound effect from
  scratch (no samples), timed from the events the stage records; it masters to −14 LUFS.
- `banner.html` draws the README banners and the social card:
  `node render.mjs --lang en --dpr 2 --shot "banner.html?theme=dark" --out out/readme/banner-en-dark.png`.

Where the outputs are used: the README banners and walkthrough images live in `docs/readme/`, and the film, poster and
social card on the website live in `website/public/assets/media/` — copy new renders there.

Requirements: macOS with Google Chrome, Node, Python 3, ffmpeg. SF Symbols and file icons are exported from the
local system by `tools/export-assets.swift`; the photos were generated for this video and the cut-out was made with
Pickverter's own Remove BG.


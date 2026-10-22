# Pickverter promo video

A 35-second launch video rendered entirely from code — the stage, the motion and the sound — in 16:9 and 9:16.

```bash
promo/make.sh                         # English and Korean, 16:9 and 9:16
promo/make.sh ko                      # one language
PROMO_ASPECTS=v promo/make.sh ko      # only the vertical cut
PROMO_PLATFORMS=win promo/make.sh     # the Windows cut (PROMO_PLATFORMS="mac win" for both)
```

Outputs land in `promo/out/`, all 60 fps:

- `Pickverter-promo-<lang>-4k.mp4` (3840×2160) and `-1080p.mp4`
- `Pickverter-promo-<lang>-vertical-4k.mp4` (2160×3840) and `-vertical.mp4` (1080×1920) for Reels, Shorts and TikTok.
  The vertical cut keeps captions and keys inside the platforms' safe area (nothing important in the bottom 400 px).

The edit runs on one beat grid (128 BPM, `window.MUSIC`) so energy only climbs until the icon. The wheel gets time to be
read — about 1.3–2 s from opening to the pick, with unhurried pointer moves — and `MUSIC.shift` gives the score two more
bars of demo before the montage. Result-card chimes sit under the music.

| Bars | Scene |
| --- | --- |
| 0 | Hook — "Every file. One key away." and the ⌥ key |
| 1–3 | Three iPhone photos → JPG at once (marquee, the wheel blooms open) |
| 3–5 | Word → PDF |
| 5–7 | Rotate: hover a tool and its options fan out |
| 7–10 | Remove the background → clipboard → paste into a chat |
| 10–12½ | AirDrop the PDF to an iPhone |
| 12½–15 | Montage on blue, one kind of file every two beats — video, audio, PDF, documents, photos, files: a live tool in the disc, its name, and four more tools as chips |
| 16–18 | Fly out of the picked slot into the app icon, name, "by ivLis STUDIO", last hit |

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

## The Windows cut

`win.html` and `src/win/` tell the same 31 seconds on Windows 11, on the same beat grid and score: File Explorer and the
Alt key, WebP → JPG, Word → PDF, rotate, cut-out → Ctrl+V, then the share sheet sends the travel plan to an Android phone
through Phone Link. Outputs are named `Pickverter-promo-windows-<lang>-….mp4`.

- The wheel and result cards follow the Windows app (`windows/src/Pickverter/UI`: `WheelCanvas.cs`, `Cards.cs`,
  `Theme.cs`) — rings, hub text, the ⇧ / Ctrl / Alt tabs, the #005FB8 accent, 368-wide cards in the bottom-right corner
  whose buttons fold to icons instead of wrapping. The finale zooms out of the Windows icon (`src/win/icon.js`, the same
  numbers as `windows/scripts/make-icons.mjs`).
- Icons are Fluent UI System Icons (MIT), subset into `assets/fluent/` by `tools/fluent-glyphs.py` from the app's own
  symbol map (`windows/scripts/icons.json`) plus the Explorer, share sheet and phone chrome.
- Windows type (Segoe UI, Malgun Gothic, Segoe Sans Display) isn't redistributable: `render.mjs --platform win` reads it
  from this Mac's Microsoft Office install and falls back to system fonts elsewhere (`--fonts` lists what it found).
  Korean titles use Pretendard when it is installed; the phone uses Roboto.

Requirements: macOS with Google Chrome, Node, Python 3, ffmpeg. SF Symbols and file icons are exported from the
local system by `tools/export-assets.swift`; the photos were generated for this video and the cut-out was made with
Pickverter's own Remove BG.


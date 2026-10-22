<p align="center">
  <a href="https://ivlis-studio.github.io/pickverter/">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="docs/readme/banner-en-dark.png">
      <img src="docs/readme/banner-en-light.png" width="880" alt="Pickverter by ivLis STUDIO — pick a file, tap one key, and finish right at your cursor.">
    </picture>
  </a>
</p>

<p align="center">
  <a href="https://github.com/ivLis-Studio/pickverter/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/ivLis-Studio/pickverter?style=flat-square&label=release&color=0A84FF"></a>
  <a href="https://github.com/ivLis-Studio/pickverter/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/ivLis-Studio/pickverter/total?style=flat-square&label=downloads&color=1D1D1F&cacheSeconds=3600"></a>
  <img alt="macOS 14+" src="https://img.shields.io/badge/macOS-14%2B-1D1D1F?style=flat-square&logo=apple&logoColor=white">
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-1D1D1F?style=flat-square"></a>
  <a href="https://github.com/ivLis-Studio/pickverter/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/ivLis-Studio/pickverter/ci.yml?branch=main&style=flat-square&label=CI"></a>
</p>

<p align="center">
  <a href="https://github.com/ivLis-Studio/pickverter/releases/latest"><b>Download</b></a> ·
  <a href="https://ivlis-studio.github.io/pickverter/"><b>Website</b></a> ·
  <a href="#install">Install</a> ·
  <a href="CHANGELOG.md">Changelog</a> ·
  <a href="README.ko.md">한국어</a>
</p>

<!-- The film is a GitHub attachment, so it plays inline here. Source: promo/ -->
https://github.com/user-attachments/assets/feb93e2b-2654-4059-9b74-615e5dddc990

<p align="center"><sub>35 seconds · sound on · every frame and every sound rendered from code — <a href="promo/">see how</a></sub></p>

## How it works

<table>
  <tr>
    <td width="50%" valign="top"><img src="docs/readme/step-en-1.webp" alt="A photo selected in Finder while Option is tapped"><br><b>1 · Pick a file, tap ⌥</b><br>Select files in Finder and tap Option — or ⌥-click a file, or tap ⇧ ⌘ ⌃ while dragging.</td>
    <td width="50%" valign="top"><img src="docs/readme/step-en-2.webp" alt="The convert ring open at the cursor with PNG highlighted"><br><b>2 · The wheel opens at your cursor</b><br>Point at a slot. The center always shows what a click will do.</td>
  </tr>
  <tr>
    <td width="50%" valign="top"><img src="docs/readme/step-en-3.webp" alt="The rotate slot with its options fanned out on the outer ring"><br><b>3 · Hover to choose how</b><br>Slots with options fan them out — rotate direction, size, quality…</td>
    <td width="50%" valign="top"><img src="docs/readme/step-en-4.webp" alt="A result card in the corner and the new PNG next to the original"><br><b>4 · Done, right there</b><br>The result lands next to the original or on the clipboard, with a card to show, open, copy or undo.</td>
  </tr>
</table>

## Features

<table>
  <tr>
    <td valign="top" width="33%"><b>⇧ Convert</b><br><sub>Images · video · audio · PDF · documents · EPUB · Office &amp; iWork · tables · subtitles · archives</sub></td>
    <td valign="top" width="33%"><b>⌘ Tools</b><br><sub>Remove background · hide faces &amp; text · compress · resize · crop · OCR · rotate · trim · split &amp; join · PDF pages · lock · info</sub></td>
    <td valign="top" width="33%"><b>⌃ Actions</b><br><sub>AirDrop · Terminal · code editor · copy path · copy file · move · float · share · checksums</sub></td>
  </tr>
</table>

- **Convert** images (JPG, PNG, HEIC, WebP, AVIF, TIFF, GIF, BMP, PDF, ICO), video (MP4, MOV, WebM, MKV, GIF, MP3, M4A, WAV),
  audio (MP3, M4A, WAV, FLAC, AIFF, OGG, Opus), PDF (PNG, JPG, TXT, MD, DOCX, RTF, HTML), documents (PDF, DOCX, RTF, TXT, MD, HTML, ODT, **EPUB**),
  Excel/Numbers/Pages/Keynote/PowerPoint, tables (CSV, TSV, JSON, MD, HTML), subtitles (SRT, VTT, TXT), archives (ZIP, TAR.GZ)
- **Edit** with small, focused editors: trim video and audio, crop with ratio presets, hide areas (mosaic, blur, fill — finds faces and text for you),
  organize PDF pages by drag and drop, edit photo/PDF/media info (losslessly for photos where possible)
- **Tools** compress, resize, remove background, extract text (OCR), rotate and flip, collage, picture-book EPUB · video compress, resolution,
  speed, mute, snapshot, split, join · audio loudness, silence trim, mono, visualizer video · PDF merge, split, extract pages, reverse,
  remove blank pages, lock · images and media out of Office/iWork files · ZIP / TAR.GZ
- **Actions** AirDrop, open in Terminal or your code editor, copy path (full, ~, name, shell-quoted), copy, move/copy to recent folders,
  float as an always-on-top preview, share, checksums (SHA-256, SHA-1, MD5)

**Results where you want them** — a file, the clipboard, both, or the wheel asks every time (Settings › General).
⌥-click / ⌘C sends one result to the clipboard, ⌘S to a file. Originals are never overwritten.

**Make it yours** — right-click a slot to hide it, move it, pin one of its options to the main ring, or choose what a plain click runs.

<p align="center">
  <img src="docs/screenshots/wheel-tools.png" width="400" alt="The tools ring with the rotate options fanned out">
  <img src="docs/screenshots/cards.png" width="400" alt="Result cards">
</p>

<details>
<summary><b>Keyboard</b></summary>

| Gesture | |
| --- | --- |
| Select files in Finder → **tap ⌥** | wheel for the selection |
| **⌥-click** a file | wheel for that file |
| While dragging files, **tap ⇧ / ⌘ / ⌃** | Convert / Tools / Actions ring — drop on a slot |
| ← → · 1–0 | move around the wheel · run a slot by number |
| ↑ ↓ or move outward | a slot's options on the outer ring |
| type `png`, `mp3`… | jump to a format |
| Tab or ⇧ ⌘ ⌃ | switch rings |
| ↩︎ · esc | run · close |

</details>

## Install

1. Download **Pickverter-x.y.z.dmg** from [Releases](https://github.com/ivLis-Studio/pickverter/releases/latest) and drag Pickverter into Applications.
2. The app isn't notarized yet, so macOS asks once: open **System Settings › Privacy & Security** and click **Open Anyway**
   (or right-click Pickverter › Open).
3. Allow **Accessibility** (to notice the ⌥ tap and drags) and **Finder** control (to read your selection). The welcome window walks you through it.
   It then offers **File access**: turn on **Full Disk Access** (recommended — no prompts for any folder or drive), or allow
   just Desktop, Documents, Downloads and iCloud Drive. Doing it up front means macOS never stops a job halfway to ask.
4. Optional: [ffmpeg](https://ffmpeg.org) unlocks more video and audio formats — `brew install ffmpeg` (the welcome window has a button for it).
   Pages, Keynote, Numbers or LibreOffice, when installed, enable high-quality Office/iWork exports.

Requires macOS 14 or later, Apple silicon or Intel.

### Updates

Pickverter checks this repository's releases about once a day and installs updates with one click (or in the background,
ready on the next restart — Settings › Updates). Every update is verified before it is installed: an Ed25519 signature
over the release, its size and SHA-256, the bundle identifier and the code signature.

Because releases are currently signed ad hoc (no Apple Developer ID yet), macOS treats each new version as a new app and asks
for its permissions (Accessibility, Finder control, file access) again after an update. Pickverter clears the stale entries
for you and reopens the welcome window so you can switch them back on.

### Privacy

Files never leave your Mac. The only network request is the update check to github.com, which you can turn off.

## Languages

English · 한국어 · 日本語 · 简体中文 · 繁體中文 · Español · Français · Deutsch · Русский · Português (Brasil).
Switch any time in Settings › General. All strings live in `Resources/i18n.json`; corrections are welcome.

## Build from source

```sh
scripts/build-app.sh            # release build → build/Pickverter.app
scripts/build-app.sh debug --run
scripts/test.sh                 # i18n check, wheel self-test, ~60 engine cases on generated fixtures
```

The build calls `swiftc` directly (no Xcode project needed). With only the Command Line Tools installed it picks an SDK that works without
SwiftUI macros. `Package.swift` is there for Xcode/SwiftPM users.

Locally built apps are signed ad hoc, so after a rebuild you may need to remove Pickverter from the Accessibility list and add it again.
Set `PICKVERTER_SIGN_IDENTITY` to sign with a stable certificate.

Developer commands: `build/Pickverter.app/Contents/MacOS/Pickverter cli …` — `list`, `run <option-id> <files>`, `snapshot`, `editor-snapshot`,
`selftest`, `fixtures`, `inspect`. Editors can be answered from the environment (`PICKVERTER_EDITOR_CROP='{"rect":…}'`),
which is how the tests run them without windows. `PICKVERTER_LANG=ja` picks a language, `PICKVERTER_DEBUG=1` logs gestures.

## Releasing

1. Add a `## [x.y.z]` section to `CHANGELOG.md` — it becomes the release notes and the text in the update window.
2. Tag and push: `git tag vx.y.z && git push origin vx.y.z`. The **Release** workflow tests, builds a universal app,
   creates the DMG, the ZIP and the signed `update.json`, and publishes the GitHub release.
   (Locally: `scripts/release.sh x.y.z --publish`.)

The update signing key is the `PICKVERTER_UPDATE_KEY` repository secret (its public half is `PVUpdatePublicKey` in
`Resources/Info.plist`). Keep a backup: installed copies only accept updates signed with it.
Optional secrets for a Developer ID build: `MACOS_CERT_P12`, `MACOS_CERT_PASSWORD`, `MACOS_SIGN_IDENTITY`,
and for notarization `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD`.

## Project layout

- `site/` the website at [ivlis-studio.github.io/pickverter](https://ivlis-studio.github.io/pickverter/) (GitHub Pages)
- `promo/` the launch film, rendered from code — `promo/make.sh`

- `Input/` event tap (modifier taps, ⌥-click, drags), Finder selection
- `Wheel/` the wheel panel, outer option ring, drop target, rendering
- `Core/` option model, catalog per ring, wheel customization, work queue, localization
- `Engines/` images (ImageIO, Vision), media (ffmpeg, AVFoundation), PDF (PDFKit), documents, EPUB, Office/iWork, data, archives
- `UI/` result cards, editors, settings, menu bar, onboarding, update window
- `App/` app lifecycle, preferences, updater, CLI

## License

[MIT](LICENSE) © ivLis Studio



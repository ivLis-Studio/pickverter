# Changelog · Windows

Notable changes to Pickverter for Windows. Versions follow [Semantic Versioning](https://semver.org) and match the Mac app.
The section for a version becomes the Windows part of the GitHub release and the notes in the in-app updater.

## [0.3.1] - 2026-10-24

- **Nothing new on Windows** — this release signs and notarizes the Mac app. Windows keeps the same version number as the Mac, so updating changes nothing here.

## [0.3.0] - 2026-10-24

- **Files that cross between Windows and Macs** — ZIPs made on a Mac now unpack with readable names (no letters split apart, no __MACOSX folder), and so do ZIPs from older tools and from PowerShell's Compress-Archive. ZIPs from Pickverter open cleanly on a Mac, keep empty folders and leave Thumbs.db and desktop.ini out, and nothing in an archive can land outside the folder it unpacks to.
- **Text** — Korean EUC-KR text reads right in every conversion, and the new **Fix Encoding** tool rewrites it as UTF-8. CSVs start with a byte-order mark, so Excel reads every letter, and SMI subtitles convert to SRT, VTT and TXT.
- **Join Split Letters** — names from a Mac that show up as ㅎㅏㄴ instead of 한 are joined from the Actions ring; a name another file already has is left alone. Every file Pickverter writes gets a name that reads right everywhere.
- **See the result first** — point at Compress, Resize or a picture format and the wheel's center shows the new size before you click (≈ 509 KB · −40%). Compress can also aim for a size: under 500 KB to 5 MB for pictures, 10 to 100 MB for video, or a size of your own. **Compare** on a picture's result card shows the original and the result side by side.
- **HWP and HWPX** — Hangul documents convert to PDF, DOCX, HWP ↔ HWPX, PNG, JPG, TXT, Markdown and HTML, with Copy Text and Count. Locked documents ask for their password, distribution documents work too, and PDFs keep their Korean text on PCs without Korean fonts. The free rhwp engine downloads on first use.
- **Your wheel, in Settings** — Settings › Wheel lays out each ring per file type with a live preview: reorder slots, remove them, give an option its own slot or bring one in from another ring, then use the layout for every file type.
- **Fixes** — PDF Compress works on PDFs from Chrome, Word, macOS and LibreOffice (it failed with "Object is not a name"), PDFs with only an owner password can be compressed, rotated, locked and edited, and damaged PDFs are rebuilt. Names with "%" convert, .icns opens, WebM falls back to AV1 on Arm, Mute keeps the original format, snapshots work for TS, MPEG and WMV, a lone .bz2 or .xz unpacks with 7-Zip. Dialogs stay above result cards, and Korean wraps at spaces in the wheel.
- **License** — Pickverter is free and MIT licensed again.

## [0.2.1] - 2026-10-23

- **ffmpeg in one click** — Pickverter now downloads ffmpeg itself: from the welcome window, Settings › Integrations, or simply by clicking a format that needs it — the job downloads ffmpeg on its own card and carries on. It is the build for your PC (x64 or Arm64), used only after it matches the SHA-256 GitHub publishes for it. winget is no longer needed; Settings can remove the downloaded copy, and uninstalling Pickverter removes it too.
- **The wheel at screen edges** — near an edge, a corner or the taskbar the whole wheel now moves inward, so every slot, the outer options and the ring tabs stay visible and clickable.
- **What you see is what a click does** — a highlighted slot always runs when clicked, and a click where nothing is highlighted closes the wheel.
- **Small and zoomed displays** — the welcome window, Settings and the editors shrink to fit the screen (a 1080p laptop at 150 % included), so the buttons along the bottom stay reachable. The tray flyout scrolls instead of running off the screen, and opens beside its icon wherever the taskbar is — top, left, right or auto-hidden.
- **Trim** — typing a start or end time on a very short clip no longer fails.

## [0.2.0] - 2026-10-23

First Windows release.

- **The wheel on Windows** — tap Alt with files selected in File Explorer or on the desktop, Alt+click a file, or tap Shift, Ctrl or Alt while dragging files. Three rings: Convert, Tools, Actions. Prefer Ctrl or Right Ctrl as the trigger? Change it in Settings › Shortcuts.
- **Windows keys everywhere** — Enter, Esc, Tab, Ctrl+C and Ctrl+S for one result, Alt+click, Del in the editors. Key names follow your keyboard (Strg, Maj, Mayús…).
- **Native look** — Fluent icons, Segoe UI Variable, your accent color, light and dark mode, Windows 11 shadows and rounded corners. Result cards stay in the corner you choose, and their buttons never wrap.
- **Windows features** — Share (Nearby sharing, Mail…), Open With, Windows Terminal, PowerShell or Command Prompt, undo to the Recycle Bin, Windows OCR and face detection, Media Foundation for MP4, M4A, MP3 and WAV without ffmpeg, Microsoft Office or LibreOffice for Office exports, HEIC export with the Store extensions.
- **Set up once** — the welcome window checks Windows Security's folder protection and offers to allow Pickverter, installs ffmpeg with winget, and starts Pickverter with Windows.
- **Updates** — a per-user installer without administrator rights; Pickverter checks GitHub Releases and installs signed updates itself.
- **10 languages** — English, 한국어, 日本語, 简体中文, 繁體中文, Español, Français, Deutsch, Русский, Português (Brasil).


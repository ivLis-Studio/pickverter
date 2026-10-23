# Changelog · Windows

Notable changes to Pickverter for Windows. Versions follow [Semantic Versioning](https://semver.org) and match the Mac app.
The section for a version becomes the Windows part of the GitHub release and the notes in the in-app updater.

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


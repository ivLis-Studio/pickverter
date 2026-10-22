# Changelog

All notable changes to Pickverter. Versions follow [Semantic Versioning](https://semver.org).
The section for a version becomes the release notes shown in the in-app updater.

## [Unreleased]

- **ffmpeg in one click** — Pickverter now downloads ffmpeg itself: from the welcome window, Settings › Integrations, or simply by clicking a format that needs it — the job downloads ffmpeg on its own card and carries on. It is a signed static build, used only after its published SHA-256 and its builder's signature check out. Homebrew is no longer needed, and Settings can remove the downloaded copy again.
- **The wheel at screen edges** — near an edge or a corner the whole wheel now moves inward, so every slot, the outer options and the ring tabs stay on screen and clickable, clear of the menu bar and the Dock.
- **What you see is what a click does** — a highlighted slot always runs when clicked, and a click where nothing is highlighted closes the wheel. Before, the area just outside the disc could highlight a slot while a click there closed the wheel.
- **Several displays** — the wheel, result cards and floating previews now pick the right display when the pointer sits on the far edge of a screen.
- **Editors fit the screen** — editor windows shrink to the screen they open on, so their buttons never end up below the Dock on smaller Macs.

## [0.2.0] - 2026-10-23

- **Pickverter for Windows** — the same wheel now runs on Windows 10 and 11: tap Alt in File Explorer. The installer is on this release page, next to the Mac download.
- **New icon** — the app icon, menu bar glyph and in-app mark now share one design: the wheel with its picked slot and the pointer that picked it.
- **Website** — Pickverter now has a home at [ivlis-studio.github.io/pickverter](https://ivlis-studio.github.io/pickverter/): try the wheel right in your browser, watch the film and find install help for Mac and Windows, in English and Korean.
- **License** — from this version Pickverter is licensed under [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/): free to share unchanged, non-commercially, with credit. Earlier versions were released under the MIT License.

## [0.1.1] - 2026-10-22

- **File access up front** — the welcome window and Settings › Integrations now offer Full Disk Access (recommended) or just your Desktop, Documents, Downloads and iCloud Drive, so macOS no longer interrupts a conversion to ask. Existing users see this step once.
- **Clear failures** — a job that hits a folder Pickverter may not read now says which place is blocked and offers **Grant Access…**, which opens the right page in System Settings. Files are checked before any work starts.
- **Updates** — an update now also clears stale Finder control and file-access entries, not only Accessibility, and the welcome window shows what to switch back on.
- **Polish** — folder labels wrap instead of being cut off in longer languages; Settings no longer shows the Office & iWork section twice.

## [0.1.0] - 2026-10-22

First public release.

- **The wheel** — tap ⌥ with files selected in Finder, ⌥-click a file, or tap ⇧ ⌘ ⌃ while dragging. Three rings: Convert, Tools, Actions. Hover a slot to fan out its options (rotate direction, resize size, GIF quality…).
- **Your wheel** — right-click a slot to hide it, move it, pin an option to the main ring or choose what a click runs. Each slot can remember the option you used last.
- **Results where you want them** — save as a file, copy to the clipboard, both, or ask every time. ⌥-click / ⌘C / ⌘S override it once.
- **New editors** — trim video and audio (QuickTime-style), crop images and video with ratio presets, hide areas with mosaic / blur / fill (auto-detects faces and text), organize PDF pages by drag and drop, edit photo/PDF/media info.
- **New tools** — split and join video and audio, collage, audio visualizer video, EPUB from documents or images, PDF page tools (extract range, reverse, remove blank pages, odd/even).
- **Office & iWork** — Excel → CSV/TSV/JSON/Markdown/HTML and PowerPoint → Markdown/text built in; PDF/Word/PowerPoint exports through Pages, Keynote, Numbers or LibreOffice when installed; extract every image inside.
- **Everyday polish** — a work queue keeps heavy jobs from fighting over the CPU, results are staged invisibly next to the original, the wheel stays out of the way while you rename a file, result cards stay on the screen you worked on in the corner you choose, VoiceOver announcements.
- **Updates** — Pickverter checks GitHub Releases and installs signed updates itself.
- **10 languages** — English, 한국어, 日本語, 简体中文, 繁體中文, Español, Français, Deutsch, Русский, Português (Brasil).

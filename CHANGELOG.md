# Changelog

All notable changes to Pickverter. Versions follow [Semantic Versioning](https://semver.org).
The section for a version becomes the release notes shown in the in-app updater.

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

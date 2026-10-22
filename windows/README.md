# Pickverter for Windows

The Windows version of Pickverter: pick files in File Explorer, tap one key, and convert, edit or share them from a wheel
that opens right at your cursor. Same features, rings and languages as the [Mac app](../macos), adapted to Windows keys,
icons and conventions.

## Install

1. Download **Pickverter-x.y.z-win-x64-setup.exe** (or **win-arm64** for Snapdragon PCs) from
   [Releases](https://github.com/ivLis-Studio/pickverter/releases/latest) and run it. It installs for your account only —
   no administrator rights — into `%LOCALAPPDATA%\Programs\Pickverter`.
2. The installer isn't code-signed yet, so SmartScreen may show **"Windows protected your PC"**: click **More info › Run anyway**.
3. The welcome window checks the few things worth setting up front: Windows Security's **controlled folder access**
   (Pickverter offers to allow itself when it is on), **ffmpeg** (optional — one click downloads it) and **Start with Windows**.
   Nothing else needs a permission — Windows lets Pickverter notice key taps and drags right away.

Pickverter lives in the notification area. Click its icon for the quick panel, right-click for the menu.
A portable `Pickverter-x.y.z-win-<arch>.zip` is on every release too.

Requires Windows 10 version 2004 or later, or Windows 11 (x64 or Arm64).

## Keys

| | Mac | Windows |
| --- | --- | --- |
| Open the wheel for the selection | tap ⌥ in Finder | **tap Alt** in File Explorer or on the desktop (Ctrl or Right Ctrl in Settings) |
| Open it for one file | ⌥-click | **Alt+click** |
| While dragging files | tap ⇧ / ⌘ / ⌃ | **tap Shift / Ctrl / Alt** → Convert / Tools / Actions |
| Switch rings while the wheel is open | Tab · ⇧ ⌘ ⌃ | **Tab · Shift Ctrl Alt** (tap the trigger again to close) |
| Run · close | ↩︎ · esc | **Enter · Esc** |
| One result to the clipboard / a file | ⌥-click · ⌘C / ⌘S | **Alt+click · Ctrl+C / Ctrl+S** |
| Move around · options · jump | ← → 1–0 · ↑ ↓ · type a format | same |
| Delete an area or a page in an editor | ⌫ | **Del** |

Key names follow your keyboard: Strg and Umschalt in German, Maj and Échap in French, Mayús and Supr in Spanish.
Tapping Alt in File Explorer normally shows the menu's key tips; Pickverter only takes the tap when files are selected,
and gives it back otherwise.

## What's different on Windows

- **File Explorer** replaces Finder: the wheel reads the selection of the active tab (Windows 11 tabs included) or the desktop,
  and stays out of the way while you rename a file or type in the address bar.
- **Actions**: Share (Nearby sharing, Mail and other apps) instead of AirDrop, Open With, Windows Terminal / PowerShell 7 /
  Windows PowerShell / Command Prompt / Git Bash, VS Code / Cursor / Zed / Sublime / Notepad++ (or Notepad), Undo sends results to the Recycle Bin.
- **Engines**: Windows OCR and face detection, the Windows PDF renderer, Microsoft Edge for HTML → PDF, Media Foundation for MP4, M4A,
  MP3 and WAV without ffmpeg, Microsoft Office (Word, Excel, PowerPoint) or LibreOffice for faithful Office exports,
  HEIC export with the HEIF + HEVC extensions from the Microsoft Store. Background removal downloads its model (178 MB) once.
- **Look**: Fluent UI System Icons, Segoe UI Variable, your accent color, light and dark mode, Windows 11 window shadows and
  rounded corners. Result cards appear in the corner you choose (bottom right by default) and their buttons never wrap.

Files never leave your PC. The only network requests are the update check and, if you use it, the one-time model download.

## Updates

Pickverter checks this repository's releases about once a day. It downloads `update-windows.json`, verifies the Ed25519
signature over the version, size and SHA-256 of the build, checks the downloaded zip, then swaps its folder (keeping the old one
until the new version starts) and relaunches. A 404 means no Windows build in the latest release: that counts as up to date.

## Build from source

You need the .NET 10 SDK. The app runs on Windows; the core (engines, wheel logic, updater) and the tests also build and run on
macOS and Linux, and the whole solution compiles anywhere thanks to `EnableWindowsTargeting`.

```powershell
cd windows
scripts/build.ps1 -Run              # debug build, then start it
scripts/test.ps1                    # strings, unit tests, glyphs, Windows self test
scripts/test.ps1 -Snapshots out     # …plus PNG renders of every window in six languages, light and dark
```

On a Mac: `dotnet build Pickverter.slnx` and `dotnet test --project tests/Pickverter.Core.Tests/Pickverter.Core.Tests.csproj`.

Developer commands: `Pickverter.exe cli …` — `list`, `run <option-id> <files>`, `fixtures`, `selftest`, `glyphs`,
`snapshot`, `screenshot`, `e2e` (drives File Explorer, the Alt tap and the wheel on a real desktop), `inspect` and the `update-*` tools. Editors can be answered from the environment
(`PICKVERTER_EDITOR_CROP='{"rect":…}'`), `PICKVERTER_LANG=ja` picks a language and `PICKVERTER_DEBUG=1` writes a trace to
`%LOCALAPPDATA%\Pickverter\Logs`.

## Releasing

Both apps ship in the same GitHub release. Add a `## [x.y.z]` section to [CHANGELOG.md](CHANGELOG.md) (and to
[../macos/CHANGELOG.md](../macos/CHANGELOG.md)), then push the tag `vx.y.z`. The **Windows Release** workflow tests, publishes
self-contained x64 and Arm64 builds, makes the installers with Inno Setup, signs `update-windows.json` with the
`PICKVERTER_UPDATE_KEY` secret (the same key as the Mac updater; it refuses to publish a signature installed copies
wouldn't accept) and attaches everything to the release, whichever workflow creates it first.
Locally: `scripts/release.ps1 -Version x.y.z -Publish` (needs Inno Setup 6: `winget install JRSoftware.InnoSetup`).

## Layout

- `src/Pickverter.Core/` — everything that doesn't need Windows: option catalog, wheel model, engines (Magick.NET, PDFsharp,
  PdfPig, ONNX Runtime, ffmpeg), documents, preferences, updater. Strings come from `macos/Resources/i18n.json` plus the
  Windows overlay [`Resources/i18n.windows.json`](Resources/i18n.windows.json).
- `src/Pickverter/` — the WPF app: `Input/` (low-level hooks, File Explorer selection), `UI/` (wheel, cards, tray,
  settings, welcome, editors), `Services/` (Windows implementations of every platform service), `Cli/`.
- `tests/` — xUnit tests of the core.
- `installer/` — the Inno Setup script and wizard images. `scripts/` — build, test, release, string checks, icon and glyph generators.

Icons are drawn by `scripts/make-icons.mjs` (same mark as the Mac app, with a Windows arrow); `scripts/make-glyphs.mjs`
subsets [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT) for the symbols in
`scripts/icons.json`.


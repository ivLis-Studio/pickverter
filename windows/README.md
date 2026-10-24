# Pickverter for Windows

The Windows version of Pickverter: pick files in File Explorer, tap one key, and convert, edit or share them from a wheel
that opens right at your cursor. Same features, rings and languages as the [Mac app](../macos), adapted to Windows keys,
icons and conventions.

<!-- The Windows film is a GitHub attachment, so it plays inline here. Source: promo/win.html -->
https://github.com/user-attachments/assets/e47fc193-fc9c-48ff-b08b-e94a4da85e0c

<p align="center"><sub>35 seconds · turn the sound on · <a href="../README.ko.md#windows에서는">한국어 영상</a></sub></p>

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
  HWP and HWPX documents go through [rhwp](https://github.com/edwardkim/rhwp) (MIT), the open-source HWP engine, which
  Pickverter downloads (about 20 MB, pinned version and SHA-256) the first time you convert one; on Arm64 PCs it runs under
  Windows 11's x64 emulation.
- **Look**: Fluent UI System Icons, Segoe UI Variable, your accent color, light and dark mode, Windows 11 window shadows and
  rounded corners. Result cards appear in the corner you choose (bottom right by default) and their buttons never wrap.

Files never leave your PC. The only network requests are the update check (GitHub edition) and, if you use them, the
one-time downloads (ffmpeg, rhwp, the background-removal model).

## Updates

The GitHub edition (installer or zip) checks this repository's releases about once a day. It downloads
`update-windows.json`, verifies the Ed25519 signature over the version, size and SHA-256 of the build, checks the
downloaded zip, then swaps its folder (keeping the old one until the new version starts) and relaunches. A 404 means no
Windows build in the latest release: that counts as up to date. The Microsoft Store edition leaves updates to the Store.

## Microsoft Store edition

The same app, built from the same source, as an MSIX package (Store ID 9P05JBV4QDGC). Pickverter notices at startup that it
runs from its package (`GetCurrentPackageFullName`) and then:

- **The Store updates it.** Pickverter's own updater is off: no update checks, no Check for Updates…, no Software Update
  window or update cards. Settings › Updates shows the version and says updates come from the Microsoft Store.
- **Start with Windows** is the package's startup task instead of the Run key. Turned off in Task Manager › Startup apps
  (or Settings › Apps › Startup), only the user can turn it back on there; Settings and the welcome window say so.
- **Its files live in the package's own folder**, `%LOCALAPPDATA%\Packages\ivLis-STUDIO.Pickverter_<id>\LocalCache`:
  settings in `Roaming\Pickverter`, logs, the clipboard cache and the downloads (ffmpeg, rhwp, the model) in
  `Local\Pickverter`, scratch files in `Local\Temp`. Spelled out like that the paths are real for every program, so
  File Explorer can paste a result Pickverter copied, and uninstalling removes it all. Settings start fresh.
- **Controlled folder access**: a Store app doesn't ask for administrator rights, so Pickverter opens Windows Security and
  says what to do there (Allow an app through Controlled folder access › Recently blocked apps). Every Store update
  installs into a new folder, so Windows may ask again after one.
- One Pickverter runs at a time, whichever edition. The Store edition has no command-line alias: `Pickverter.exe cli …`
  is for the GitHub and source builds (`cli edition` shows which edition and folders a copy uses).
  `PICKVERTER_CHANNEL=store` makes any build behave like the Store edition, for tests and `cli snapshot`.

Building it needs Windows with the .NET 10 SDK and the Windows SDK (makeappx, makepri, signtool):

```powershell
scripts/package-msix.ps1 -Version 0.3.1                     # dist\msix\Pickverter-0.3.1.msixbundle, plus one .msix per architecture
scripts/package-msix.ps1 -Version 0.3.1 -TestCertificate    # …and test-signed\ copies with the .cer to sideload them
```

The bundle (package version 0.3.1.0) is the Partner Center upload and stays unsigned: the Store signs it. To try a
test-signed copy in a VM, import `test-signed\Pickverter-0.3.1-test.cer` into Local Machine › Trusted People as an
administrator (`Import-Certificate -FilePath … -CertStoreLocation Cert:\LocalMachine\TrustedPeople`), then open
`test-signed\Pickverter-0.3.1.msixbundle`. The **Windows Store package** workflow does the same for every `v*` tag (or by
hand, with a version) and keeps the packages as workflow artifacts; it attaches nothing to the GitHub release. The
manifest is [`packaging/AppxManifest.xml`](packaging/AppxManifest.xml); `scripts/make-icons.mjs` draws the tiles and
logos in `packaging/Assets`.

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

Developer commands: `Pickverter.exe cli …` — `list`, `run <option-id> <files>`, `fixtures`, `selftest`, `glyphs`, `edition`,
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
  PdfPig, ONNX Runtime, ffmpeg, rhwp), documents, preferences, updater. Strings come from `macos/Resources/i18n.json` plus the
  Windows overlay [`Resources/i18n.windows.json`](Resources/i18n.windows.json).
- `src/Pickverter/` — the WPF app: `Input/` (low-level hooks, File Explorer selection), `UI/` (wheel, cards, tray,
  settings, welcome, editors), `Services/` (Windows implementations of every platform service), `Cli/`.
- `tests/` — xUnit tests of the core.
- `installer/` — the Inno Setup script and wizard images. `packaging/` — the Microsoft Store manifest, makepri settings
  and tiles. `scripts/` — build, test, release, MSIX packaging, string checks, icon and glyph generators.

Icons are drawn by `scripts/make-icons.mjs` (same mark as the Mac app, with a Windows arrow); `scripts/make-glyphs.mjs`
subsets [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT) for the symbols in
`scripts/icons.json`.


# Pickverter for Windows

Work in progress. The Mac app lives in [`../macos`](../macos).

What the Windows app can share with the Mac app:

- **Strings** — `macos/Resources/i18n.json` holds every UI string in 10 languages (plural forms are separated by `|`).
  Move it to a shared folder once both apps read it.
- **Icon** — the mark is drawn in code: `AppIconArt` in `macos/Sources/Pickverter/UI/BrandArt.swift` (and `promo/src/icon.js`).
- **Updates** — the Mac updater downloads `releases/latest/download/update.json`. Attach Windows builds to the same
  GitHub release, or publish them without marking them *latest* — otherwise Mac users stop receiving updates.

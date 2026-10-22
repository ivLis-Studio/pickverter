# pickverter.site

The Pickverter website, published to GitHub Pages from `site/` by `.github/workflows/site.yml`.

Plain HTML, CSS and JavaScript — no build step. Preview locally:

```sh
cd site && python3 -m http.server 8765    # http://127.0.0.1:8765/?lang=ko
```

- `index.html` holds the English copy; `assets/js/i18n.js` holds Korean (by `data-t` key) and the app's own strings
  for the wheel demo and the language showcase (taken from `Resources/i18n.json`).
- `assets/js/wheel.js` is the live wheel: the app's geometry, outer option rings, hit-testing and keyboard.
- `assets/media/` — the film and poster come from `promo/`; editor screenshots from `Pickverter cli editor-snapshot`.
- Icons are [Lucide](https://lucide.dev) (ISC).


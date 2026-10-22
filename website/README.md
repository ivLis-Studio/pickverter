# Website

The Pickverter website at [ivlis-studio.github.io/pickverter](https://ivlis-studio.github.io/pickverter/).
Everything in `public/` is published to GitHub Pages by `.github/workflows/website.yml` when it changes on `main`.

Plain HTML, CSS and JavaScript — no build step.

```sh
cd website
npm run serve                 # preview at http://127.0.0.1:8765/?lang=ko
npm install && npm test       # smoke test in headless Chrome: wheel, tabs, slider, languages, repository links
node tools/shot.mjs <url> <out.png> [width] [height] [fullPage] [light|dark]
```

- `public/index.html` holds the English copy. `public/assets/js/i18n.js` holds the Korean copy (by `data-t` key) and the
  app's own strings for the wheel demo and the language showcase, copied from `macos/Resources/i18n.json`.
- `public/assets/js/wheel.js` is the live wheel: the app's geometry, outer option rings, hit-testing and keyboard.
- `public/assets/media/` — the film and poster come from `promo/`; editor screenshots from `Pickverter cli editor-snapshot`.
- Icons are [Lucide](https://lucide.dev) (ISC).

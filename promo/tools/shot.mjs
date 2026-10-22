// Still screenshot of a local page: node tools/shot.mjs <page.html> <out.png> [width] [height] [js-to-eval]
import { chromium } from "playwright-core";
import path from "node:path";

const [page, out, w = "1600", h = "900", script] = process.argv.slice(2);
const browser = await chromium.launch({ channel: "chrome", headless: true, args: ["--force-color-profile=srgb"] });
const tab = await browser.newPage({ viewport: { width: +w, height: +h }, deviceScaleFactor: 1 });
await tab.goto("file://" + path.resolve(page));
await tab.evaluate(() => document.fonts.ready);
if (script) await tab.evaluate(script);
await tab.waitForTimeout(150);
await tab.screenshot({ path: out });
await browser.close();
console.log(out);


import { chromium } from "playwright-core";
const [url, out, w = "1440", h = "900", full = "1", scheme = "light", wait = "600"] = process.argv.slice(2);
const b = await chromium.launch({ channel: "chrome", headless: true, args: ["--force-color-profile=srgb"] });
const p = await b.newPage({ viewport: { width: +w, height: +h }, deviceScaleFactor: 1, colorScheme: scheme, reducedMotion: "reduce" });
const errs = [];
p.on("pageerror", (e) => errs.push("pageerror " + e.message));
p.on("console", (m) => { if (m.type() === "error") errs.push("console " + m.text()); });
await p.goto(url, { waitUntil: "networkidle" });
await p.evaluate(() => document.querySelectorAll(".reveal").forEach((n) => n.classList.add("in")));
// Walk the page once so lazy images load.
await p.evaluate(async () => { for (let y = 0; y < document.documentElement.scrollHeight; y += innerHeight) { scrollTo(0, y); await new Promise((r) => setTimeout(r, 60)); } scrollTo(0, 0); });
await p.waitForTimeout(+wait);
await p.screenshot({ path: out, fullPage: full === "1" });
const info = await p.evaluate(() => ({ h: document.documentElement.scrollHeight, overflow: document.documentElement.scrollWidth > innerWidth, ver: document.querySelector(".dl-version")?.textContent }));
console.log(JSON.stringify({ out, ...info, errs }));
await b.close();


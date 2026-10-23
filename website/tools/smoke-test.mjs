// Smoke test for the website in headless Chrome. Serves public/ itself, so no other server is needed.
//   cd website && npm install && npm test
import { chromium } from "playwright-core";
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.join(here, "../public");
const repo = path.join(here, "../..");

const TYPES = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css", ".png": "image/png", ".webp": "image/webp", ".mp4": "video/mp4", ".json": "application/json" };
const server = http.createServer((req, res) => {
  let p = decodeURIComponent(new URL(req.url, "http://x").pathname);
  if (p.endsWith("/")) p += "index.html";
  const file = path.join(root, p);
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { "Content-Type": TYPES[path.extname(file)] || "application/octet-stream" });
  fs.createReadStream(file).pipe(res);
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const base = "http://127.0.0.1:" + server.address().port + "/";

const failures = [];
function check(name, ok, detail = "") {
  console.log((ok ? "PASS " : "FAIL ") + name + (detail ? "  — " + detail : ""));
  if (!ok) failures.push(name);
}

const browser = await chromium.launch({ channel: "chrome", headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = [];
page.on("pageerror", (e) => errors.push(e.message));
await page.goto(base + "?lang=en", { waitUntil: "networkidle" });

// The live wheel: switch rings, fan out options, run one.
const box = await page.locator("#wheel > svg.wsvg").boundingBox();
const k = box.width / 480;
const at = (lx, ly) => [box.x + lx * k, box.y + ly * k];
const pol = (r, d) => at(240 + Math.cos((d * Math.PI) / 180) * r, 240 + Math.sin((d * Math.PI) / 180) * r);
const hub = async () => (await page.locator(".whub").innerText()).replace(/\n/g, " | ");
await page.mouse.click(...at(240, 240 + 158 + 12 + 17));
check("wheel: Tools tab", (await page.locator(".wtab.on").innerText()).includes("Tools"));
await page.mouse.move(...pol(106, 150), { steps: 4 });
check("wheel: hover shows Rotate", (await hub()).startsWith("Rotate"), await hub());
check("wheel: outer ring opens", (await page.locator(".w-sub.open").count()) === 1);
await page.mouse.move(...pol(191, 150), { steps: 4 });
await page.mouse.move(...pol(191, 128), { steps: 4 });
check("wheel: outer option targeted", (await hub()).includes("Left 90°"), await hub());
await page.mouse.click(...pol(191, 128));
await page.waitForTimeout(300);
check("wheel: result card", (await page.locator(".wcard.show").count()) === 1, (await page.locator(".wcard").innerText()).replace(/\n/g, " | "));
await page.mouse.move(10, 10);
await page.locator("#wheel").focus();
await page.keyboard.press("Shift");
check("wheel: ⇧ switches to Convert", (await page.locator(".wtab.on").innerText()).includes("Convert"));

// The rest of the page.
await page.locator("#tab-actions").click();
check("rings: Actions panel", await page.locator("#panel-actions").isVisible());
await page.locator("#ba-range").fill("20");
check("before/after slider", (await page.locator("#ba").evaluate((e) => e.style.getPropertyValue("--x"))) === "20%");
await page.locator('.dest[data-dest="clipboard"]').click();
check("destinations", (await page.locator("#dest-sub-out").innerText()) === "On the clipboard");
await page.locator('#langs button[data-l="ja"]').click();
check("language showcase", (await page.locator("#lang-show").innerText()).includes("変換"));
const film = () => page.locator("#film-video source").getAttribute("src");
check("film: the Mac cut", (await film()) === "assets/media/film-en.mp4", await film());

// Links into this repository must point at files that exist.
const repoLinks = await page.$$eval("a[href]", (as) => as.map((a) => a.href).filter((h) => /github\.com\/ivLis-Studio\/pickverter\/(blob|tree)\/main\//.test(h)));
for (const href of repoLinks) {
  const rel = decodeURIComponent(href.replace(/^.*\/(blob|tree)\/main\//, "").split("#")[0]);
  check("repo link " + rel, fs.existsSync(path.join(repo, rel)));
}

// Korean copy.
await page.locator("#lang-select").selectOption("ko");
check("Korean title", (await page.title()).includes("모든 파일을"));
check("Korean wheel", (await page.locator(".wtab.on").innerText()).includes("변환"));
const untranslated = await page.$$eval("[data-t]", (ns) => ns.filter((n) => /[A-Za-z]{4,}/.test(n.textContent) && !/[가-힣]/.test(n.textContent)).map((n) => n.dataset.t));
const allowed = new Set(["film.title"]);
check("Korean copy complete", untranslated.filter((k) => !allowed.has(k) && !/^(g\.|t\.|a\.)/.test(k)).length === 0, untranslated.join(", "));

// The Windows page: ?os=win shows the installer, Windows keys and Windows install steps; the switch goes back.
await page.goto(base + "?lang=en&os=win", { waitUntil: "networkidle" });
check("Windows: platform", (await page.evaluate(() => document.documentElement.dataset.os)) === "win");
check("Windows: download button", (await page.locator(".hero .dl-link").innerText()).includes("Windows"));
check("Windows: title key", (await page.locator(".hero h1").innerText()).includes("Alt"));
check("Windows: wheel keys", (await page.locator(".wtab").first().innerText()).includes("Shift"));
check("Windows: install steps", (await page.locator("ol.only-win").isVisible()) && !(await page.locator("ol.only-mac").isVisible()));
check("Windows: ffmpeg in one click", (await page.locator('ol.only-win [data-t="instw.4.d"]').innerText()).includes("one click"));
check("Windows: the Windows film", (await film()) === "assets/media/film-win-en.mp4", await film());
check("Windows: its poster", (await page.locator("#film-video").getAttribute("poster")) === "assets/media/poster-win-en.webp");
for (const name of ["film-win-en.mp4", "film-win-ko.mp4", "poster-win-en.webp", "poster-win-ko.webp"]) {
  const response = await page.request.get(base + "assets/media/" + name);
  check("Windows: " + name + " is served", response.ok() && (await response.body()).length > 10000);
}
await page.mouse.move(10, 10);
await page.locator("#wheel").focus();
await page.keyboard.press("Control");
check("Windows: Ctrl switches to Tools", (await page.locator(".wtab.on").innerText()).includes("Tools"));
const winMissing = await page.evaluate(() => Object.keys(window.SITE_WIN.en).filter((k) => !(k in window.SITE_WIN.ko)));
check("Windows: Korean wording complete", winMissing.length === 0, winMissing.join(", "));
await page.locator("#lang-select").selectOption("ko");
check("Windows: Korean download button", (await page.locator(".hero .dl-link").innerText()).includes("Windows용"));
check("Windows: Korean film", (await film()) === "assets/media/film-win-ko.mp4", await film());
const winUntranslated = await page.$$eval("[data-t]", (ns) => ns.filter((n) => n.offsetParent && /[A-Za-z]{4,}/.test(n.textContent) && !/[가-힣]/.test(n.textContent)).map((n) => n.dataset.t));
check("Windows: Korean copy complete", winUntranslated.filter((k) => !allowed.has(k) && !/^(g\.|t\.|a\.|ring\.k\.)/.test(k)).length === 0, winUntranslated.join(", "));
await page.locator('#os-switch button[data-os="mac"]').click();
check("switch back to Mac", (await page.locator(".hero .dl-link").innerText()).includes("Mac") && (await page.locator(".wtab").first().innerText()).includes("⇧"));
check("switch back to Mac: the Mac film", (await film()) === "assets/media/film-ko.mp4", await film());

check("no page errors", errors.length === 0, errors.join(" | "));
await browser.close();
server.close();
console.log(failures.length ? failures.length + " failed" : "all passed");
process.exit(failures.length ? 1 : 0);

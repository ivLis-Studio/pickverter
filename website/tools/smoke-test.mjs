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
  const type = TYPES[path.extname(file)] || "application/octet-stream", size = fs.statSync(file).size;
  // Byte ranges, as GitHub Pages serves them, so the film can start and seek.
  const range = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range || "");
  if (range) {
    const start = +range[1], end = range[2] ? Math.min(+range[2], size - 1) : size - 1;
    if (start >= size) { res.writeHead(416, { "Content-Range": "bytes */" + size }); res.end(); return; }
    res.writeHead(206, { "Content-Type": type, "Content-Range": "bytes " + start + "-" + end + "/" + size, "Accept-Ranges": "bytes", "Content-Length": end - start + 1 });
    fs.createReadStream(file, { start, end }).pipe(res);
    return;
  }
  res.writeHead(200, { "Content-Type": type, "Content-Length": size, "Accept-Ranges": "bytes" });
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

// The film plays muted on its own once it's on screen; Pause holds it; Sound on starts it over with sound and controls.
// The time is compared unrounded: the first frame past 0.5 s reads 0.50 once rounded, which made the first check flaky.
const filmState = () => page.evaluate(() => {
  const v = document.querySelector("#film-video");
  return { muted: v.muted, paused: v.paused, controls: v.controls, t: v.currentTime, ambient: document.querySelector("#player").classList.contains("ambient") };
});
const playing = (after) => page.waitForFunction((t) => { const v = document.querySelector("#film-video"); return !v.paused && v.currentTime > t; }, after, { timeout: 10000 }).catch(() => {});
await page.locator("#player").scrollIntoViewIfNeeded();
await playing(0.5);
let st = await filmState();
check("film: plays muted on its own", st.ambient && st.muted && !st.paused && st.t > 0.5, JSON.stringify(st));
await page.locator("#film-pause").click();
await page.evaluate(() => window.scrollTo(0, 0));
await page.waitForTimeout(400);
await page.locator("#player").scrollIntoViewIfNeeded();
await page.waitForTimeout(700);
st = await filmState();
check("film: Pause holds it while scrolling", st.ambient && st.paused, JSON.stringify(st));
await page.locator("#film-sound").click();
await playing(0);
st = await filmState();
check("film: Sound on starts over with sound", !st.ambient && !st.muted && !st.paused && st.controls && st.t < 4, JSON.stringify(st));

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

// The Windows page links to the Store, even after release info arrives; direct installers remain available.
await page.goto(base + "?lang=en&os=win", { waitUntil: "networkidle" });
check("Windows: platform", (await page.evaluate(() => document.documentElement.dataset.os)) === "win");
check("Windows: Store download button", (await page.locator(".hero .dl-link").innerText()).includes("Microsoft Store") && (await page.locator(".hero .dl-link").getAttribute("href")) === "https://apps.microsoft.com/detail/9P05JBV4QDGC");
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
check("Windows: Korean Store download button", (await page.locator(".hero .dl-link").innerText()).includes("Store에서 받기"));
check("Windows: Korean film", (await film()) === "assets/media/film-win-ko.mp4", await film());
const winUntranslated = await page.$$eval("[data-t]", (ns) => ns.filter((n) => n.offsetParent && /[A-Za-z]{4,}/.test(n.textContent) && !/[가-힣]/.test(n.textContent)).map((n) => n.dataset.t));
check("Windows: Korean copy complete", winUntranslated.filter((k) => !allowed.has(k) && !/^(g\.|t\.|a\.|ring\.k\.)/.test(k)).length === 0, winUntranslated.join(", "));
await page.locator('#os-switch button[data-os="mac"]').click();
check("switch back to Mac", (await page.locator(".hero .dl-link").innerText()).includes("Mac") && (await page.locator(".wtab").first().innerText()).includes("⇧"));
check("switch back to Mac: the Mac film", (await film()) === "assets/media/film-ko.mp4", await film());

// Phones: no sideways scrolling on either page, also with the system text size turned up (Android font size, Safari's aA).
// The release comes from GitHub once and is replayed, so the download card shows real version text.
const latest = await fetch("https://api.github.com/repos/ivLis-Studio/pickverter/releases/latest").then((r) => (r.ok ? r.text() : null)).catch(() => null);
for (const width of [320, 390]) {
  const phone = await browser.newContext({ viewport: { width, height: 760 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true });
  await phone.route("https://api.github.com/**", (r) => (latest ? r.fulfill({ status: 200, contentType: "application/json", body: latest }) : r.abort()));
  for (const os of ["win", "mac"]) for (const lang of ["ko", "en"]) for (const size of [100, 130]) {
    const p = await phone.newPage();
    p.on("pageerror", (e) => errors.push(e.message));
    await p.goto(base + "?lang=" + lang + "&os=" + os, { waitUntil: "networkidle" });
    if (size !== 100) await p.addStyleTag({ content: "html{-webkit-text-size-adjust:" + size + "%!important;text-size-adjust:" + size + "%!important}" });
    await p.waitForTimeout(150);
    const m = await p.evaluate(() => ({ W: document.documentElement.clientWidth, S: document.documentElement.scrollWidth }));
    check("phone " + width + "px, " + os + " " + lang + ", text " + size + "%: no sideways scroll", m.S <= m.W, "page " + m.S + "px wide on a " + m.W + "px screen");
    // The Remove BG result card keeps its title readable, and the "just this once" shortcuts sit beside or below it,
    // never on top of it (they used to squeeze the card into a one-letter column on phones).
    const card = await p.evaluate(() => {
      const d = document.querySelector(".dest-demo"), c = d.querySelector(".card"), b = c.querySelector("b"), o = d.querySelector(".over");
      const rc = c.getBoundingClientRect(), rb = b.getBoundingClientRect(), ro = o.getBoundingClientRect(), s = getComputedStyle(b);
      const line = parseFloat(s.lineHeight) || parseFloat(s.fontSize) * 1.4;
      return { lines: Math.round(rb.height / line), apart: ro.top >= rc.bottom - 1 || ro.left >= rc.right - 1, width: Math.round(rc.width) };
    });
    check("phone " + width + "px, " + os + " " + lang + ", text " + size + "%: result card readable", card.lines <= 2 && card.apart && card.width >= 200, JSON.stringify(card));
    await p.close();
  }
  await phone.close();
}

check("no page errors", errors.length === 0, errors.join(" | "));
await browser.close();
server.close();
console.log(failures.length ? failures.length + " failed" : "all passed");
process.exit(failures.length ? 1 : 0);

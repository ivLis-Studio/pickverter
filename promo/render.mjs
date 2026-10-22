// Renders the promo stage to stills or video.
//   node render.mjs --stills 3.4,4.9 [--lang en] [--dpr 1]
//   node render.mjs --video out/video-en.mp4 [--lang en] [--dpr 2] [--fps 60] [--workers 6] [--from 0] [--to 35]
//   node render.mjs --events out/events-en.json [--lang en]
import { chromium } from "playwright-core";
import sharp from "sharp";
import { spawn } from "node:child_process";
import fs from "node:fs";
import http from "node:http";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const opt = {};
for (let i = 0; i < argv.length; i++) if (argv[i].startsWith("--")) opt[argv[i].slice(2)] = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : true;
const lang = opt.lang || "en";
const dpr = +(opt.dpr || 1);
const fps = +(opt.fps || 60);

// The app's own strings, so the wheel and cards read exactly like Pickverter.
const i18n = JSON.parse(fs.readFileSync(path.join(here, "../macos/Resources/i18n.json"), "utf8"));
const i18nJS = "window.I18N = " + JSON.stringify(i18n) + ";\n";
const i18nFile = path.join(here, "src/i18n-data.js");
// Chunk processes start together: only write when it changed, so no page ever reads a half-written file.
if (!fs.existsSync(i18nFile) || fs.readFileSync(i18nFile, "utf8") !== i18nJS) fs.writeFileSync(i18nFile, i18nJS);

// CSS masks (SF Symbols) need http: file:// pages can't load them.
const TYPES = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css", ".png": "image/png", ".jpg": "image/jpeg", ".json": "application/json", ".ttf": "font/ttf" };
// San Francisco straight from macOS, so Latin text never falls back to the locale's UI font.
const SYSTEM_FONTS = { "/sysfonts/SFNS.ttf": "/System/Library/Fonts/SFNS.ttf", "/sysfonts/SFNSMono.ttf": "/System/Library/Fonts/SFNSMono.ttf" };
const server = http.createServer((req, res) => {
  const pathname = decodeURIComponent(new URL(req.url, "http://x").pathname);
  if (pathname === "/favicon.ico") { res.writeHead(204); res.end(); return; }
  const file = SYSTEM_FONTS[pathname] || path.join(here, pathname);
  if ((!SYSTEM_FONTS[pathname] && !file.startsWith(here)) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { "Content-Type": TYPES[path.extname(file)] || "application/octet-stream", "Cache-Control": "max-age=3600" });
  fs.createReadStream(file).pipe(res);
});
await new Promise((r) => server.listen(0, "127.0.0.1", r));
const origin = "http://127.0.0.1:" + server.address().port;

async function openPage(browser, url = "/index.html", size = { width: 1920, height: 1080 }) {
  const page = await browser.newPage({ viewport: size, deviceScaleFactor: dpr });
  page.on("pageerror", (e) => console.error("page error:", e.message));
  page.on("console", (m) => { if (m.type() === "error") console.error("console:", m.text()); });
  await page.addInitScript("window.LANG = " + JSON.stringify(lang) + ";");
  if (opt.clean) await page.addInitScript("window.CLEAN = true;");
  await page.goto(origin + url);
  await page.evaluate(() => window.ready);
  const cdp = await page.context().newCDPSession(page);
  return { page, cdp };
}

async function frame({ page, cdp }, t) {
  await page.evaluate((t) => { window.renderAt(t); return new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))); }, t);
  // The clip scale sets the output resolution: 1920×1080 CSS pixels × dpr.
  const { data } = await cdp.send("Page.captureScreenshot", { format: "png", optimizeForSpeed: true, clip: { x: 0, y: 0, width: 1920, height: 1080, scale: dpr } });
  return Buffer.from(data, "base64");
}

// Motion blur: average several sub-frames across a 180° shutter (half a frame) centred on t.
let blurWindows = [];
const baseSamples = +(opt.samples || 1);
function samplesAt(t) {
  for (const w of blurWindows) if (t >= w.from && t < w.to) return Math.max(w.samples, baseSamples);
  return baseSamples;
}
async function blurredFrame(p, t) {
  const n = samplesAt(t);
  if (n <= 1) return frame(p, t);
  const shutter = 0.5 / fps;
  let acc = null, info = null;
  for (let k = 0; k < n; k++) {
    const png = await frame(p, t - shutter / 2 + (shutter * (k + 0.5)) / n);
    const { data, info: i } = await sharp(png).raw().toBuffer({ resolveWithObject: true });
    info = i;
    if (!acc) acc = new Uint16Array(data.length);
    for (let j = 0; j < data.length; j++) acc[j] += data[j];
  }
  const out = Buffer.allocUnsafe(acc.length);
  for (let j = 0; j < acc.length; j++) out[j] = (acc[j] + (n >> 1)) / n;
  return sharp(out, { raw: { width: info.width, height: info.height, channels: info.channels } }).png({ compressionLevel: 1 }).toBuffer();
}

const browser = await chromium.launch({ channel: "chrome", headless: true, args: ["--force-color-profile=srgb", "--disable-lcd-text", "--hide-scrollbars"] });

// A single still of any page (README banners): --shot "banner.html?theme=dark" --out file.png --width 1600 --height 600
if (opt.shot) {
  const { page } = await openPage(browser, "/" + opt.shot, { width: +(opt.width || 1600), height: +(opt.height || 600) });
  await page.waitForTimeout(100);
  await page.screenshot({ path: opt.out, omitBackground: true });
  console.log(opt.out);
}

if (opt.events) {
  const { page } = await openPage(browser);
  const events = await page.evaluate(() => window.AUDIO_EVENTS);
  fs.writeFileSync(opt.events, JSON.stringify({ duration: await page.evaluate(() => window.DURATION), events }, null, 1));
  console.log(opt.events, events.length, "events");
}

if (opt.stills) {
  const p = await openPage(browser);
  blurWindows = await p.page.evaluate(() => window.MOTION_BLUR || []);
  fs.mkdirSync(path.join(here, "out/stills"), { recursive: true });
  for (const t of String(opt.stills).split(",").map(Number)) {
    const out = path.join(here, "out/stills", lang + "-" + t.toFixed(2) + ".png");
    fs.writeFileSync(out, await (opt.blur ? blurredFrame(p, t) : frame(p, t)));
    console.log(out);
  }
}

const crf = opt.crf || (dpr > 1 ? "14" : "15");

// One chunk of frames [first, last) → an H.264 file. Runs in its own process so sub-frame averaging uses its own core.
if (opt.chunk) {
  const from = +opt.from, first = +opt.first, last = +opt.last;
  const p = await openPage(browser);
  blurWindows = await p.page.evaluate(() => window.MOTION_BLUR || []);
  const ff = spawn("ffmpeg", ["-v", "error", "-y", "-f", "image2pipe", "-framerate", String(fps), "-c:v", "png", "-i", "-",
    "-c:v", "libx264", "-preset", "slow", "-crf", crf, "-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
    "-threads", "2", "-x264-params", "keyint=" + fps * 2, opt.chunk], { stdio: ["pipe", "inherit", "inherit"] });
  for (let f = first; f < last; f++) {
    const buf = await blurredFrame(p, from + f / fps);
    if (!ff.stdin.write(buf)) await new Promise((r) => ff.stdin.once("drain", r));
    if ((f - first + 1) % 30 === 0) console.log("+30");
  }
  ff.stdin.end();
  await new Promise((r) => ff.on("close", r));
}

if (opt.video) {
  const from = +(opt.from || 0), to = +(opt.to || (await (await openPage(browser)).page.evaluate(() => window.DURATION)));
  const total = Math.round((to - from) * fps);
  const workers = Math.max(1, +(opt.workers || 6));
  // Interleave short chunks across workers so the expensive finale is shared.
  const size = Math.ceil(total / (workers * 3));
  const tmp = fs.mkdtempSync(path.join(here, "out/.chunks-"));
  const started = Date.now();
  let done = 0;
  const queue = [];
  for (let first = 0, i = 0; first < total; first += size, i++) queue.push({ first, last: Math.min(total, first + size), file: path.join(tmp, "c" + String(i).padStart(3, "0") + ".mp4") });
  const files = queue.map((q) => q.file);
  const pass = ["--lang", lang, "--dpr", String(dpr), "--fps", String(fps), "--samples", String(baseSamples), "--crf", crf, "--from", String(from)];
  async function worker() {
    while (queue.length) {
      const q = queue.shift();
      await new Promise((resolve, reject) => {
        const child = spawn(process.execPath, [fileURLToPath(import.meta.url), "--chunk", q.file, "--first", String(q.first), "--last", String(q.last), ...pass], { stdio: ["ignore", "pipe", "inherit"] });
        child.stdout.on("data", (d) => {
          done += 30 * (String(d).match(/\+30/g) || []).length;
          const el = (Date.now() - started) / 1000;
          process.stdout.write("frames ~" + done + "/" + total + "  " + (done / el).toFixed(1) + " fps  eta " + Math.round(((total - done) * el) / Math.max(1, done)) + "s\n");
        });
        child.on("close", (code) => (code === 0 ? resolve() : reject(new Error("chunk failed " + q.file))));
      });
    }
  }
  await Promise.all(Array.from({ length: workers }, worker));
  const list = path.join(tmp, "list.txt");
  fs.writeFileSync(list, files.map((f) => "file '" + f + "'").join("\n"));
  await new Promise((r) => spawn("ffmpeg", ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", opt.video], { stdio: "inherit" }).on("close", r));
  console.log(opt.video, "in", Math.round((Date.now() - started) / 1000) + "s");
  console.log("chunks:", tmp);
}

await browser.close();
server.close();


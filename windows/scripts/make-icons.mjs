#!/usr/bin/env node
// Draws the Windows app icon, the tray icons, the installer images and the Microsoft Store (MSIX) assets (same mark as
// the Mac app: macos/Sources/Pickverter/UI/BrandArt.swift, with a Windows arrow and a Windows-style plate).
//
//   node scripts/make-icons.mjs        (uses sharp from ../promo/node_modules and ImageMagick for BMPs)
import { createRequire } from "node:module";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const require = createRequire(join(root, "../promo/package.json"));
const sharp = require("sharp");

const PLATE = "#16171A", SLOT = "#2C2E34", PICK = "#0A84FF", HUB = "#F4F4F6";
const ARROW = [[0, 0], [0, 0.842], [0.21, 0.632], [0.368, 1.0], [0.474, 0.947], [0.316, 0.579], [0.579, 0.579]];
const N = 8, PICKED = 1;
const rad = (d) => (d * Math.PI) / 180;
const P = (cx, cy, r, a) => [cx + Math.cos(a) * r, cy + Math.sin(a) * r];
const f = (n) => +n.toFixed(2);

/// Core of a rounded annular sector; drawn with a round-joined stroke of 2*corner to round it (like the app).
function segment(cx, cy, startDeg, endDeg, inner, outer, gap, corner) {
  const ri = inner + corner, ro = outer - corner, half = gap / 2 + corner;
  const s = rad(startDeg), e = rad(endDeg), dso = half / ro, dsi = half / ri;
  const [x1, y1] = P(cx, cy, ro, s + dso), [x2, y2] = P(cx, cy, ro, e - dso);
  const [x3, y3] = P(cx, cy, ri, e - dsi), [x4, y4] = P(cx, cy, ri, s + dsi);
  const largeO = e - dso - (s + dso) > Math.PI ? 1 : 0, largeI = e - dsi - (s + dsi) > Math.PI ? 1 : 0;
  return `M${f(x1)} ${f(y1)} A${f(ro)} ${f(ro)} 0 ${largeO} 1 ${f(x2)} ${f(y2)} L${f(x3)} ${f(y3)} A${f(ri)} ${f(ri)} 0 ${largeI} 0 ${f(x4)} ${f(y4)} Z`;
}

function docPath(cx, cy, w, h) {
  const x = cx - w / 2, y = cy - h / 2, fo = w * 0.34, r = w * 0.12;
  return `M${x + r} ${y} L${x + w - fo} ${y} L${x + w} ${y + fo} L${x + w} ${y + h - r} Q${x + w} ${y + h} ${x + w - r} ${y + h} L${x + r} ${y + h} Q${x} ${y + h} ${x} ${y + h - r} L${x} ${y + r} Q${x} ${y} ${x + r} ${y} Z`;
}

function arrowPath(x, y, h) {
  return ARROW.map(([px, py], i) => (i ? "L" : "M") + f(x + px * h) + " " + f(y + py * h)).join(" ") + " Z";
}

/// The full mark on the 1024 grid. detail: 2 = everything, 1 = no document, 0 = plate and ring only.
function iconSvg(size, detail, shadows) {
  const span = 360 / N;
  const plate = detail === 2 ? 864 : detail === 1 ? 944 : 1000;
  const inset = (1024 - plate) / 2;
  const radius = plate * (detail === 0 ? 0.2 : 0.174);
  const inner = detail === 0 ? 170 : 150, outer = detail === 0 ? 400 : detail === 1 ? 352 : 312;
  const gap = detail === 0 ? 46 : 26, corner = detail === 0 ? 14 : 21;
  let body = "";
  for (let i = 0; i < N; i++) {
    const mid = -90 + i * span;
    const lift = i === PICKED && detail > 0 ? 24 : 0;
    const [dx, dy] = P(0, 0, lift, rad(mid));
    const d = segment(512 + dx, 512 + dy, mid - span / 2, mid + span / 2, inner, outer, gap, corner);
    const fill = i === PICKED ? PICK : SLOT;
    const shadow = i === PICKED && shadows ? ' filter="url(#ps)"' : "";
    body += `<path d="${d}" fill="${fill}" stroke="${fill}" stroke-width="${corner * 2}" stroke-linejoin="round"${shadow}/>`;
  }
  if (detail > 0) body += `<circle cx="512" cy="512" r="${detail === 2 ? 102 : 96}" fill="${HUB}"/>`;
  if (detail === 2) {
    const w = 98, h = 126, x = 512 + w / 2, y = 512 - h / 2, fo = w * 0.34;
    body += `<path d="${docPath(512, 512, w, h)}" fill="${PLATE}"/>`;
    body += `<path d="M${x - fo} ${y} L${x - fo} ${y + fo} L${x} ${y + fo}" fill="none" stroke="${HUB}" stroke-width="${w * 0.07}" stroke-linejoin="round"/>`;
  }
  if (detail > 0) {
    const mid = rad(-90 + span * PICKED);
    const reach = (inner + outer) / 2 + 26;
    const [tx, ty] = P(512, 512, reach, mid);
    const height = detail === 2 ? 190 : 250;
    const shadow = shadows ? ' filter="url(#cs)"' : "";
    body += `<path d="${arrowPath(tx - 4, ty - 4, height)}" fill="#FFFFFF" stroke="${PLATE}" stroke-width="${detail === 2 ? 15 : 22}" stroke-linejoin="round"${shadow}/>`;
  }
  const defs = shadows
    ? '<defs><filter id="ts" x="-20%" y="-20%" width="140%" height="140%"><feDropShadow dx="0" dy="12" stdDeviation="14" flood-color="#000" flood-opacity="0.28"/></filter>' +
      '<filter id="ps" x="-40%" y="-40%" width="180%" height="180%"><feDropShadow dx="0" dy="10" stdDeviation="14" flood-color="#000" flood-opacity="0.4"/></filter>' +
      '<filter id="cs" x="-50%" y="-50%" width="200%" height="200%"><feDropShadow dx="0" dy="8" stdDeviation="10" flood-color="#000" flood-opacity="0.35"/></filter></defs>'
    : "";
  const plateShadow = shadows ? ' filter="url(#ts)"' : "";
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 1024 1024">${defs}` +
    `<rect x="${inset}" y="${inset}" width="${plate}" height="${plate}" rx="${radius}" fill="${PLATE}"${plateShadow}/>` +
    `<rect x="${inset + 2}" y="${inset + 2}" width="${plate - 4}" height="${plate - 4}" rx="${radius - 2}" fill="none" stroke="#FFFFFF" stroke-opacity="0.09" stroke-width="4"/>` +
    `${body}</svg>`;
}

/// Monochrome tray glyph: the eight-slot ring with the picked slot solid.
function traySvg(size, color) {
  const span = 360 / N;
  let body = "";
  for (let i = 0; i < N; i++) {
    const mid = -90 + i * span;
    const d = segment(50, 50, mid - span / 2, mid + span / 2, 19, 47, size <= 20 ? 9 : 7, 1.5);
    body += `<path d="${d}" fill="${color}" fill-opacity="${i === PICKED ? 1 : 0.42}" stroke="${color}" stroke-opacity="${i === PICKED ? 1 : 0.42}" stroke-width="3" stroke-linejoin="round"/>`;
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 100 100">${body}</svg>`;
}

async function png(svg, size) {
  return sharp(Buffer.from(svg), { density: 72 }).resize(size, size).png({ compressionLevel: 9 }).toBuffer();
}

function ico(images) {
  const header = Buffer.alloc(6 + images.length * 16);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  images.forEach(({ size, data }, i) => {
    const at = 6 + i * 16;
    header.writeUInt8(size >= 256 ? 0 : size, at);
    header.writeUInt8(size >= 256 ? 0 : size, at + 1);
    header.writeUInt8(0, at + 2);
    header.writeUInt8(0, at + 3);
    header.writeUInt16LE(1, at + 4);
    header.writeUInt16LE(32, at + 6);
    header.writeUInt32LE(data.length, at + 8);
    header.writeUInt32LE(offset, at + 12);
    offset += data.length;
  });
  return Buffer.concat([header, ...images.map((image) => image.data)]);
}

const resources = join(root, "src/Pickverter/Resources");
const installer = join(root, "installer");
const scratch = mkdtempSync(join(tmpdir(), "pickverter-icons-"));
mkdirSync(resources, { recursive: true });
mkdirSync(installer, { recursive: true });

const appSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const app = [];
for (const size of appSizes) {
  const detail = size <= 24 ? 0 : size <= 48 ? 1 : 2;
  app.push({ size, data: await png(iconSvg(size, detail, size >= 64), size) });
}
writeFileSync(join(resources, "Pickverter.ico"), ico(app));
mkdirSync(join(root, "docs"), { recursive: true });
writeFileSync(join(root, "docs/icon-256.png"), app.at(-1).data);
writeFileSync(join(root, "docs/icon-1024.png"), await png(iconSvg(1024, 2, true), 1024));

const traySizes = [16, 20, 24, 32, 40, 48];
for (const [name, color] of [["Tray-Light.ico", "#1B1B1B"], ["Tray-Dark.ico", "#FFFFFF"]]) {
  const images = [];
  for (const size of traySizes) images.push({ size, data: await png(traySvg(size, color), size) });
  writeFileSync(join(resources, name), ico(images));
}

// Inno Setup wizard images (24-bit BMP): the mark on white, and a tall panel in the plate colour.
for (const [size, name] of [[55, "wizard-small.bmp"], [110, "wizard-small-200.bmp"]]) {
  const file = join(scratch, name.replace(".bmp", ".png"));
  writeFileSync(file, await png(iconSvg(size, size <= 64 ? 1 : 2, false), size));
  execFileSync("magick", [file, "-background", "#FFFFFF", "-flatten", "BMP3:" + join(installer, name)]);
}
for (const [w, h, name] of [[164, 314, "wizard-large.bmp"], [328, 628, "wizard-large-200.bmp"]]) {
  const mark = await png(iconSvg(Math.round(w * 0.62), 2, false), Math.round(w * 0.62));
  const panel = await sharp({ create: { width: w, height: h, channels: 3, background: PLATE } })
    .composite([{ input: mark, gravity: "center" }]).png().toBuffer();
  const file = join(scratch, name.replace(".bmp", ".png"));
  writeFileSync(file, panel);
  execFileSync("magick", [file, "BMP3:" + join(installer, name)]);
}

// Microsoft Store (MSIX) assets in packaging/Assets, named the way makepri reads qualifiers (scripts/package-msix.ps1
// builds resources.pri from them; packaging/AppxManifest.xml refers to Assets\Square44x44Logo.png and so on). Flat: the
// mark without the shadows the larger .ico sizes have, on transparent tiles (BackgroundColor="transparent"), with the
// same detail per pixel size as the .ico. The unplated and light-unplated taskbar forms are the same image: the dark plate
// reads on light and dark taskbars alike.
const assets = join(root, "packaging/Assets");
mkdirSync(assets, { recursive: true });
const detailAt = (px) => (px <= 24 ? 0 : px <= 48 ? 1 : 2);
const flatMark = (px) => png(iconSvg(px, detailAt(px), false), px);
const scaled = (size, scale) => Math.round((size * scale) / 100);

/// The mark centered on a transparent tile, fraction of the tile's shorter side.
async function tile(width, height, fraction) {
  const side = Math.round(Math.min(width, height) * fraction);
  return sharp({ create: { width, height, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } } })
    .composite([{ input: await flatMark(side), gravity: "center" }])
    .png({ compressionLevel: 9 })
    .toBuffer();
}

for (const scale of [100, 125, 150, 200, 400]) {
  // App list, Start and taskbar icon; Store logo (50 x 50 at 100%).
  writeFileSync(join(assets, `Square44x44Logo.scale-${scale}.png`), await flatMark(scaled(44, scale)));
  writeFileSync(join(assets, `StoreLogo.scale-${scale}.png`), await flatMark(scaled(50, scale)));
  // Windows 10 Start tiles: small (no name under it), medium, wide and large (the name sits at the bottom).
  writeFileSync(join(assets, `SmallTile.scale-${scale}.png`), await tile(scaled(71, scale), scaled(71, scale), 0.66));
  writeFileSync(join(assets, `Square150x150Logo.scale-${scale}.png`), await tile(scaled(150, scale), scaled(150, scale), 0.5));
  writeFileSync(join(assets, `Wide310x150Logo.scale-${scale}.png`), await tile(scaled(310, scale), scaled(150, scale), 0.5));
  writeFileSync(join(assets, `LargeTile.scale-${scale}.png`), await tile(scaled(310, scale), scaled(310, scale), 0.4));
}
for (const size of [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]) {
  const image = await flatMark(size);
  for (const form of ["", "_altform-unplated", "_altform-lightunplated"]) {
    writeFileSync(join(assets, `Square44x44Logo.targetsize-${size}${form}.png`), image);
  }
}
console.log("icons written");


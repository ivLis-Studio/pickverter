// Pickverter promo — 31 s at 128 BPM, 16:9 (1920×1080) or 9:16 (1080×1920, ?aspect=v).
// renderAt(t) draws the frame for time t (seconds); nothing depends on wall time. Every cut, pop and camera move
// sits on the beat grid shared with audio/synth.py (window.MUSIC). Story: one trip — photos, a travel plan, the dog.
//   bar 0 hook · 1–2 photos → JPG · 3–4 Word → PDF · 5–6 rotate · 7–8 cut-out → chat · 9–10½ AirDrop the PDF
//   10½–13 montage (six tools, two beats each) · 14–15 icon, name · 16 last hit
(function () {
  const { el, set, attr, lerp, clamp, prog, E, spring, kf, polar } = PV;
  const params = new URLSearchParams(location.search);
  const LANG = window.LANG || params.get("lang") || "en";
  const V = (window.ASPECT || params.get("aspect")) === "v";
  const W = V ? 1080 : 1920, H = V ? 1920 : 1080;
  const docEl = document.documentElement;
  docEl.style.setProperty("--W", W + "px");
  docEl.style.setProperty("--H", H + "px");
  docEl.classList.toggle("v", V);
  const BPM = 128, BT = 60 / BPM;
  const b = (n) => n * BT;
  const DURATION = b(66);

  // ------------------------------------------------------------------ copy
  const I18N = window.I18N || { languages: ["en"], strings: {} };
  const li = Math.max(0, I18N.languages.indexOf(LANG));
  const COPY = {
    en: {
      "promo.favorites": "Favorites", "promo.recents": "Recents", "promo.applications": "Applications",
      hook1: "Every file.", hook2: "One key away.",
      c1h: "iPhone photos to JPG, all at once.", c1s: "A whole batch in one go.",
      c2h: "Word to PDF, just like that.", c2s: "PDF, Word, Excel and PowerPoint too.",
      c3h: "Sideways? Just point the way.", c3s: "Hover a tool and its options fan out.",
      c4h: "Cut it out. Paste it anywhere.", c4s: "Save a file or just copy it, right there.",
      c5h: "AirDrop it in one click.", c5s: "Share, move and preview without leaving Finder.",
      more: "And so much more",
      words: ["Trim video", "Merge PDFs", "Blur faces", "Lock PDFs", "Shrink files", "Grab text"],
      copied: "Copied", chatName: "Mina", chatInitial: "M", chatMsg: "Send me a pic of Momo!", sent: "Delivered",
      airdrop: "Travel Plan.pdf", date: "Thursday, October 22",
      docTitle: "Lisbon Trip", docSub: "Oct 24 – 27", docRows: [["Day 1", "Alfama walk"], ["Day 2", "Belém & pastéis"], ["Day 3", "Cascais beach"]],
      foot: "Free for macOS  ·  github.com/ivLis-Studio/pickverter",
    },
    ko: {
      "promo.favorites": "즐겨찾기", "promo.recents": "최근 항목", "promo.applications": "응용 프로그램",
      hook1: "모든 파일을,", hook2: "키 하나로.",
      c1h: "아이폰 사진, JPG로 한 번에.", c1s: "여러 장도 한꺼번에.",
      c2h: "Word 문서도 PDF로 바로.", c2s: "PDF·Word·Excel·PowerPoint까지.",
      c3h: "누운 사진은 방향만 쓱.", c3s: "툴에 올려 두면 옵션이 펼쳐져요.",
      c4h: "배경은 지우고, 바로 붙여넣기.", c4s: "파일로 저장할지 복사할지, 그 자리에서.",
      c5h: "AirDrop도 클릭 한 번.", c5s: "공유·이동·미리보기까지 Finder 안에서.",
      more: "그리고 더 많이",
      words: ["동영상 자르기", "PDF 합치기", "얼굴 가리기", "PDF 암호 걸기", "용량 줄이기", "글자 추출"],
      copied: "복사됨", chatName: "민아", chatInitial: "민", chatMsg: "모모 사진 보내줘!", sent: "전송됨",
      airdrop: "Travel Plan.pdf", date: "10월 22일 목요일",
      docTitle: "리스본 여행", docSub: "10월 24일 – 27일", docRows: [["1일차", "알파마 골목 산책"], ["2일차", "벨렝 탑 · 에그타르트"], ["3일차", "카스카이스 해변"]],
      foot: "macOS용 무료 앱  ·  github.com/ivLis-Studio/pickverter",
    },
  };
  const C = COPY[LANG] || COPY.en;
  function fmt(s, args) {
    return s.replace(/%(\d)\$@/g, (_, n) => args[+n - 1]).replace(/%@/g, () => args.shift());
  }
  const raw = (key) => C[key] ?? (I18N.strings[key] ? I18N.strings[key][li] : null) ?? COPY.en[key] ?? key;
  function plural(key, n) {
    const forms = raw(key).split("|");
    if (forms.length === 2) return n === 1 ? forms[0] : forms[1];
    if (forms.length === 3) { const m10 = n % 10, m100 = n % 100; return m10 === 1 && m100 !== 11 ? forms[0] : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? forms[1] : forms[2]; }
    return forms[0];
  }
  PV.t = (key, ...args) => fmt(raw(key), args.slice());
  PV.tp = (key, n, ...rest) => fmt(plural(key, n), [String(n), ...rest]);
  const T = PV.t;

  // ------------------------------------------------------------------ stage
  const UI = PV.UI, K = UI.K, G = UI.G;
  const stage = document.getElementById("stage");
  const world = el("div", { class: "abs layer world" }, stage);
  const over = el("div", { class: "abs layer" }, stage);
  const titles = el("div", { class: "abs layer" }, stage);
  const montL = el("div", { class: "abs layer" }, stage);
  const fin = el("div", { class: "abs layer fin" }, stage);
  const AUDIO = [];
  const sfx = (name, t, extra = {}) => AUDIO.push(Object.assign({ name, t: Math.round(t * 1000) / 1000 }, extra));
  const BLUR = [];
  const blur = (from, to, samples) => BLUR.push({ from, to, samples });

  // Layout per aspect. Screen-space anchors; the camera frames world points onto them.
  const LY = V ? {
    wheel: [540, 990], z: 1.55, keyY: 1560, capY: 232, hookY: [640, 784], hookKey: 1110,
    card: { x: 417, y: 500, slide: "y", origin: "50% 0" },
    hero: [540, 860, 400], chat: { x: 110, y: 1110, w: 860, h: 400, sw: 116, sh: 174, sy: 84 },
    phone: [790, 1190], phoneFrom: [0, 1000], phoneScale: 1.28,
    icon: { x: 540, y: 800, s: 560 }, nameY: 1080, byY: 1246, footY: 1342,
  } : {
    wheel: [945, 470], z: 1.12, keyY: 990, capY: 66, hookY: [292, 436], hookKey: 770,
    card: { x: 1920 - 44 - 352, y: 44, slide: "x", origin: "100% 0" },
    hero: [700, 560, 470], chat: { x: 1160, y: 230, w: 480, h: 600, sw: 150, sh: 225, sy: 110 },
    phone: [1555, 548], phoneFrom: [560, 0], phoneScale: 1,
    icon: { x: 960, y: 392, s: 410 }, nameY: 636, byY: 800, footY: 990,
  };

  // ------------------------------------------------------------------ data
  const FILES = [
    { name: "IMG_2041.HEIC", photo: "assets/lisbon.jpg", w: 90, h: 60, slot: 0 },
    { name: "IMG_2042.HEIC", photo: "assets/beach.jpg", w: 90, h: 60, slot: 1 },
    { name: "IMG_2043.HEIC", photo: "assets/latte.jpg", w: 90, h: 60, slot: 2 },
    { name: "Momo.jpg", photo: "assets/shiba.jpg", w: 58, h: 87, slot: 3 },
    { name: "Travel Plan.docx", icon: "assets/icons/docx.png", slot: 4 },
    { name: "IMG_2041.jpg", photo: "assets/lisbon.jpg", w: 90, h: 60, slot: 5 },
    { name: "IMG_2042.jpg", photo: "assets/beach.jpg", w: 90, h: 60, slot: 6 },
    { name: "IMG_2043.jpg", photo: "assets/latte.jpg", w: 90, h: 60, slot: 7 },
    { name: "Trip.mov", icon: "assets/icons/mov.png", slot: 8 },
    { name: "Travel Plan.pdf", icon: "assets/icons/pdf.png", slot: 9 },
  ];
  const fmtRing = (list) => list.map((title) => ({ title, format: true }));
  const v = (title, symbol, d) => ({ title, symbol, default: !!d });
  const RINGS = {
    convertHeic: fmtRing(["JPG", "PNG", "WebP", "AVIF", "TIFF", "GIF", "BMP", "PDF", "ICO"]),
    convertJpg: fmtRing(["PNG", "HEIC", "WebP", "AVIF", "TIFF", "GIF", "BMP", "PDF", "ICO"]),
    convertDocx: fmtRing(["PDF", "RTF", "TXT", "MD", "HTML", "ODT", "EPUB"]),
    convertPdf: fmtRing(["PNG", "JPG", "TXT", "MD", "DOCX", "RTF", "HTML"]),
    tools: [
      { title: T("tool.compress"), symbol: "arrow.down.right.and.arrow.up.left", detail: T("tool.compress.d"), variants: [v(T("q.balanced"), "photo", 1), v(T("q.high"), "photo"), v(T("q.small"), "photo")] },
      { title: T("tool.resize"), symbol: "arrow.down.forward.and.arrow.up.backward", variants: [v("50%", "photo", 1), v("25%", "photo"), v("75%", "photo")] },
      { title: T("tool.crop"), symbol: "crop", variants: [v(T("crop.custom"), "crop", 1), v("1:1", "crop"), v("16:9", "crop")] },
      { title: T("tool.removebg"), symbol: "scissors", detail: T("tool.removebg.d"), variants: [v(T("bg.transparent"), "checkerboard.rectangle", 1), v(T("bg.crop"), "crop"), v(T("bg.white"), "square.dashed")] },
      { title: T("tool.redact"), symbol: "checkerboard.rectangle", variants: [v(T("redact.custom"), "hand.point.up.left", 1), v(T("redact.auto_faces"), "person.crop.square"), v(T("redact.auto_text"), "text.viewfinder")] },
      { title: T("tool.ocr"), symbol: "text.viewfinder" },
      { title: T("tool.rotate"), symbol: "rotate.right", detail: T("tool.rotate.d"), variants: [v(T("rot.right"), "rotate.right", 1), v(T("rot.left"), "rotate.left"), v(T("rot.half"), "arrow.triangle.2.circlepath"), v(T("rot.flipH"), "arrow.left.and.right.righttriangle.left.righttriangle.right"), v(T("rot.flipV"), "arrow.up.and.down.righttriangle.up.righttriangle.down")] },
      { title: T("tool.meta"), symbol: "info.circle", variants: [v(T("meta.edit"), "info.circle", 1), v(T("strip.all"), "info.circle"), v(T("strip.gps"), "info.circle")] },
      { title: T("tool.zip"), symbol: "archivebox" },
    ],
    // A wheel tidied for everyday use (slots can be hidden or moved in the app).
    actions: [
      { title: "AirDrop", symbol: "dot.radiowaves.left.and.right", detail: T("action.airdrop.d") },
      { title: T("action.share"), symbol: "square.and.arrow.up", detail: T("action.share.d") },
      { title: T("action.copyfile"), symbol: "doc.on.doc", detail: T("action.copyfile.d") },
      { title: T("action.move"), symbol: "folder", variants: [v(T("folder.choose"), "folder", 1), v(T("folder.desktop"), "menubar.dock.rectangle"), v(T("folder.downloads"), "arrow.down.circle")] },
      { title: T("action.duplicate"), symbol: "plus.square.on.square", variants: [v(T("folder.choose"), "folder", 1), v(T("folder.desktop"), "menubar.dock.rectangle"), v(T("folder.downloads"), "arrow.down.circle")] },
      { title: T("action.float"), symbol: "pin", detail: T("action.float.d") },
      { title: T("action.copypath"), symbol: "link", variants: [v(T("path.full"), "link", 1), v(T("path.tilde"), "link"), v(T("path.name"), "link"), v(T("path.quoted"), "link")] },
    ],
    choice: [
      { title: T("dest.file"), symbol: "doc" },
      { title: T("dest.clipboard"), symbol: "doc.on.clipboard" },
      { title: T("dest.both"), symbol: "doc.on.doc" },
    ],
  };

  // ------------------------------------------------------------------ world: Finder
  const WX = 375, WY = 282;
  const finder = UI.Finder(world, FILES, WX, WY, { w: 900, h: 480, cols: 5 });
  const at = (n) => finder.at(n);
  const C42 = at("IMG_2042.HEIC"), CD = at("Travel Plan.docx"), CP = at("Travel Plan.pdf"), CM = at("Momo.jpg");
  const slot = (c, n, i, r = 106) => polar(c[0], c[1], r * K, -90 + (360 / n) * i);
  const SUB_R = (G.subInner + G.subOuter) / 2;
  const toLocal = (p) => [(p[0] - WX) / K, (p[1] - WY) / K];

  // ------------------------------------------------------------------ camera
  // Keys are world points framed onto a screen anchor at a zoom; punches add a short zoom kick on a beat.
  const camKeys = [];
  const frame = (p, s, z) => [p[0] - (s[0] - W / 2) / z, p[1] - (s[1] - H / 2) / z, z];
  const shot = (t, val, ease = E.inOutCubic) => camKeys.push([t, val, ease]);
  const punches = [];
  const punch = (t, amt) => punches.push([t, amt]);
  function camAt(t) {
    const [x, y, z0] = kf(t, camKeys);
    let z = z0;
    for (const [tp, amt] of punches) {
      const u = t - tp;
      if (u > 0 && u < 1.2) z *= 1 + amt * (1 - Math.exp(-u / 0.025)) * Math.exp(-u / 0.17);
    }
    return { x, y, z };
  }
  const toScreen = (p, t) => { const c = camAt(t); return [(p[0] - c.x) * c.z + W / 2, (p[1] - c.y) * c.z + H / 2, c.z]; };

  const Z = LY.z, A = LY.wheel;
  const res1 = [887, 527], res1A = V ? [540, 1000] : [960, 560], zr1 = V ? 1.2 : 1.0;           // the three new JPGs
  const res2 = [CD[0], 527], res2A = V ? [640, 1000] : [1060, 560], zr2 = V ? 1.4 : 1.1;          // the Word file and its PDF
  const AD = V ? A : [1000, 470];
  const momo = V ? 1.7 : 1.3;
  shot(0, frame(C42, [A[0], A[1] + H + 120], Z * 0.92));
  shot(b(2.5), frame(C42, [A[0], A[1] + H + 120], Z * 0.92));
  shot(b(3.35), frame(C42, A, Z), E.outExpo);
  shot(b(4.0), frame(C42, A, Z));
  shot(b(4.6), frame(C42, A, Z * 1.06), E.outCubic);
  shot(b(6.0), frame(C42, A, Z * 1.07));
  shot(b(7.0), frame(res1, res1A, zr1), E.inOutCubic);
  shot(b(11.0), frame(res1, res1A, zr1 * 1.04), E.linear);
  shot(b(11.75), frame(CD, AD, Z * 1.04), E.inOutExpo);
  shot(b(14.5), frame(CD, AD, Z * 1.09), E.linear);
  shot(b(15.5), frame(res2, res2A, zr2), E.inOutCubic);
  shot(b(19.25), frame(res2, res2A, zr2 * 1.04), E.linear);
  shot(b(20.0), frame(CM, A, momo), E.inOutExpo);
  shot(b(24.0), frame(CM, A, momo * 1.04), E.linear);
  shot(b(27.6), frame(CM, [A[0], A[1] + 20], momo * 1.1), E.inOutSine);
  shot(b(28.25), frame(CM, A, momo), E.outCubic);
  shot(b(30.75), frame(CM, A, momo * 1.02), E.linear);
  shot(b(31.75), frame(CM, V ? [540, 990] : [700, 520], V ? 1.1 : 1.0), E.inOutCubic);
  shot(b(35.2), frame(CM, V ? [540, 1000] : [690, 530], V ? 1.13 : 1.02), E.linear);
  shot(b(36.0), frame(CP, V ? A : [860, 620], Z * 1.03), E.inOutExpo);
  shot(b(38.0), frame(CP, V ? A : [860, 620], Z * 1.06), E.linear);
  shot(b(39.0), frame(CP, V ? [430, 880] : [720, 600], V ? 1.25 : 1.0), E.inOutCubic);
  shot(b(42.0), frame(CP, V ? [420, 860] : [700, 600], V ? 1.3 : 1.04), E.linear);
  punch(b(4.0), 0.05); punch(b(14.5), 0.035); punch(b(24.0), 0.04); punch(b(31.0), 0.035); punch(b(38.0), 0.04);
  blur(b(2.5), b(3.4), 10); blur(b(3.95), b(4.45), 8); blur(b(10.95), b(11.85), 12); blur(b(14.45), b(15.6), 6);
  blur(b(19.2), b(20.05), 12); blur(b(23.95), b(24.5), 6); blur(b(30.9), b(31.8), 6); blur(b(35.15), b(36.05), 12);

  // ------------------------------------------------------------------ cursor path (world)
  const moves = [];
  let last = { t: 0, p: [C42[0] - 330, C42[1] + 260] };
  function to(t0, t1, p, bend = 0.12, ease = E.inOutCubic) { moves.push({ kind: "line", t0, t1, a: last.p, b: p, bend, ease }); last = { t: t1, p }; }
  function arc(t0, t1, c, r, a0, a1, ease = E.inOutCubic) {
    moves.push({ kind: "arc", t0, t1, c, r: r * K, a0, a1, ease }); last = { t: t1, p: polar(c[0], c[1], r * K, a1) };
  }
  function cursorAt(t) {
    let p = moves.length ? moves[0].a : last.p;
    for (const m of moves) {
      if (t < m.t0) break;
      const u = m.ease(prog(t, m.t0, m.t1));
      if (m.kind === "line") {
        const dx = m.b[0] - m.a[0], dy = m.b[1] - m.a[1];
        const len = Math.hypot(dx, dy) || 1;
        const off = Math.sin(Math.PI * u) * m.bend * len * 0.18;
        p = [lerp(m.a[0], m.b[0], u) - (dy / len) * off, lerp(m.a[1], m.b[1], u) + (dx / len) * off];
      } else {
        p = polar(m.c[0], m.c[1], m.r, lerp(m.a0, m.a1, u));
      }
    }
    return p;
  }
  const clicks = [];
  const click = (t, sound = "click") => { clicks.push(t); sfx(sound, t); };

  // 1 · marquee over the three iPhone photos → JPG
  const M0 = finder.toStage(184, 54), M1 = finder.toStage(592, 190);
  to(b(2.85), b(3.0), M0, 0.0, E.outCubic);
  to(b(3.0), b(3.42), M1, 0.0, E.inOutSine);
  to(b(3.46), b(3.86), C42, 0.1);
  to(b(4.95), b(5.15), slot(C42, 9, 0), 0.0, E.outCubic);
  click(b(6.0));
  to(b(6.15), b(6.9), [C42[0] - 70, C42[1] + 128], 0.12);
  // 2 · Word → PDF
  to(b(11.0), b(11.72), [CD[0] + 6, CD[1] + 8], 0.1, E.inOutExpo);
  click(b(12.0));
  to(b(13.75), b(13.95), slot(CD, 7, 0), 0.0, E.outCubic);
  click(b(14.5));
  to(b(14.65), b(15.4), [CD[0] - 150, CD[1] + 96], 0.12);
  // 3 · rotate
  to(b(19.25), b(19.95), [CM[0] + 6, CM[1] + 8], 0.1, E.inOutExpo);
  click(b(20.0));
  to(b(21.9), b(22.1), slot(CM, 9, 6), 0.0, E.outCubic);
  to(b(22.45), b(22.7), polar(CM[0], CM[1], SUB_R * K, 150), 0.0);
  arc(b(22.85), b(23.45), CM, SUB_R, 150, 96);
  click(b(24.0));
  to(b(24.2), b(24.9), [CM[0] + 120, CM[1] + 150], 0.12);
  // 4 · remove background → clipboard
  to(b(27.45), b(27.9), CM, 0.1);
  to(b(28.85), b(29.05), slot(CM, 9, 3), 0.0, E.outCubic);
  click(b(29.5));
  arc(b(29.7), b(30.0), CM, 106, 30, -82);
  arc(b(30.12), b(30.42), CM, 106, -82, 26);
  click(b(30.75));
  to(b(30.95), b(31.6), [CM[0] + 150, CM[1] + 190], 0.12);
  // 5 · AirDrop the PDF
  to(b(35.2), b(35.92), [CP[0] + 6, CP[1] + 8], 0.1, E.inOutExpo);
  click(b(36.0));
  to(b(37.4), b(37.6), slot(CP, 7, 0), 0.0, E.outCubic);
  click(b(38.0));
  to(b(38.2), b(38.9), [CP[0] - 110, CP[1] + 120], 0.12);

  // which files are selected
  const HEICS = ["IMG_2041.HEIC", "IMG_2042.HEIC", "IMG_2043.HEIC"];
  function marqueeAt(t) {
    if (t < b(3.0) || t > b(3.62)) return null;
    const p = toLocal(cursorAt(Math.min(t, b(3.42))));
    const a = toLocal(M0);
    return { x0: a[0], y0: a[1], x1: p[0], y1: p[1], o: 1 - E.inQuad(prog(t, b(3.42), b(3.62))) };
  }
  function selAt(t) {
    const sel = {};
    if (t >= b(3.0) && t < b(12.0)) {
      const a = toLocal(M0), p = toLocal(cursorAt(Math.min(t, b(3.42))));
      const l = Math.min(a[0], p[0]), r = Math.max(a[0], p[0]), tp = Math.min(a[1], p[1]), bt = Math.max(a[1], p[1]);
      for (const n of HEICS) { const bx = finder.box(n); sel[n] = bx.l < r && bx.r > l && bx.t < bt && bx.b > tp ? 1 : 0; }
    } else if (t >= b(12.0) && t < b(20.0)) sel["Travel Plan.docx"] = 1;
    else if (t >= b(20.0) && t < b(36.0)) sel["Momo.jpg"] = 1;
    else if (t >= b(36.0)) sel["Travel Plan.pdf"] = 1;
    return sel;
  }

  // ------------------------------------------------------------------ wheels
  const files3 = { name: "", thumb: "assets/lisbon.jpg", count: 3 };
  const momoFile = { name: "Momo.jpg", thumb: "assets/shiba.jpg" };
  const wheels = [
    UI.Wheel(world, RINGS, { id: 1, at: C42, open: b(4.0), close: b(6.0), pick: true, bloom: true, shock: true, modes: [{ t: b(4.0), ring: "convertHeic" }], cursor: cursorAt,
      file: files3, preview: (it) => PV.tp("wheel.files_to", 3, it.title) }),
    UI.Wheel(world, RINGS, { id: 2, at: CD, open: b(12.75), close: b(14.5), pick: true, modes: [{ t: b(12.75), ring: "convertDocx" }], cursor: cursorAt,
      file: { name: "Travel Plan.docx", thumb: "assets/icons/docx.png", icon: true }, preview: (it) => "Travel Plan." + it.title.toLowerCase() }),
    UI.Wheel(world, RINGS, { id: 3, at: CM, open: b(20.75), close: b(24.0), pick: true, modes: [{ t: b(20.75), ring: "convertJpg" }, { t: b(21.0), ring: "tools" }], cursor: cursorAt,
      file: momoFile, preview: (it) => "Momo." + it.title.toLowerCase() }),
    UI.Wheel(world, RINGS, { id: 4, at: CM, open: b(28.25), close: b(30.75), pick: true, modes: [{ t: b(28.25), ring: "convertJpg" }, { t: b(28.5), ring: "tools" }], choice: { t: b(29.5), ring: "choice" },
      choiceOption: T("tool.removebg"), cursor: cursorAt, file: momoFile, preview: (it) => "Momo." + it.title.toLowerCase() }),
    UI.Wheel(world, RINGS, { id: 5, at: CP, open: b(36.75), close: b(38.0), pick: true, modes: [{ t: b(36.75), ring: "convertPdf" }, { t: b(37.0), ring: "actions" }], cursor: cursorAt,
      file: { name: "Travel Plan.pdf", thumb: "assets/icons/pdf.png", icon: true }, preview: (it) => "Travel Plan." + it.title.toLowerCase() }),
  ];
  for (const w of wheels) w.ticks.forEach((t) => sfx("tick", t));
  sfx("bloom", b(4.0));
  [b(12.75), b(20.75), b(28.25), b(36.75)].forEach((t) => sfx("open", t));
  [b(6.0), b(14.5), b(24.0), b(30.75), b(38.0)].forEach((t) => sfx("close", t));
  [b(21.0), b(28.5), b(37.0)].forEach((t) => sfx("swap", t));
  sfx("choice", b(29.5));

  // AirDrop radar pulses around the sent file (world space).
  const pulses = el("svg", { class: "abs pulses", width: 10, height: 10, style: { left: CP[0] + "px", top: CP[1] + "px", overflow: "visible" } }, world);
  const pulseRings = [0, 1, 2].map(() => el("circle", { cx: 0, cy: 0, r: 10, fill: "none", stroke: UI.ACCENT }, pulses));
  const PULSE = [b(38.0), b(38.5), b(39.0)];
  PULSE.forEach((t, i) => sfx("ping", t, { i }));

  const cursor = UI.Cursor(world);

  // ------------------------------------------------------------------ overlays
  const KEY = { opt: ["⌥", "option"], cmd: ["⌘", "command"], ctrl: ["⌃", "control"], v: ["V", "", true] };
  const keyGroups = [
    { keys: [["opt", b(12.3), b(12.5)]], hide: b(13.2) },
    { keys: [["opt", b(20.3), b(20.5)], ["cmd", b(20.6), b(21.0)]], hide: b(21.6) },
    { keys: [["opt", b(27.8), b(28.0)], ["cmd", b(28.1), b(28.5)]], hide: b(29.1) },
    { keys: [["cmd", b(32.7), b(33.0)], ["v", b(32.75), b(33.0)]], hide: b(33.6) },
    { keys: [["opt", b(36.3), b(36.5)], ["ctrl", b(36.6), b(37.0)]], hide: b(37.6) },
  ];
  const keycaps = [];
  for (const g of keyGroups) {
    g.keys.forEach(([k, show, press], i) => {
      const [glyph, word, letter] = KEY[k];
      keycaps.push({ cap: UI.Keycap(over, glyph, word, false, !!letter), g, i, show, press });
      if (k !== "v") sfx("key", press);
    });
  }

  const cardAt = { x: LY.card.x, y: LY.card.y, slide: LY.card.slide, origin: LY.card.origin };
  const cards = [
    UI.Card(over, Object.assign({}, cardAt, { in: b(6.25), done: b(7.25), out: b(10.5), symbol: "photo", thumb: "assets/lisbon.jpg",
      runTitle: T("job.running", T("job.convert_label", "JPG")), runSub: PV.tp("wheel.items", 3),
      doneTitle: T("job.done", T("job.convert_label", "JPG")), doneSub: PV.tp("job.files_created", 3) + " · 7.4 MB",
      buttons: [{ title: T("card.reveal"), symbol: "folder" }, { title: T("card.open"), symbol: "arrow.up.forward.app" }, { title: T("card.copy"), symbol: "doc.on.clipboard" }, { title: T("card.undo"), symbol: "arrow.uturn.backward" }] })),
    UI.Card(over, Object.assign({}, cardAt, { in: b(14.75), done: b(15.5), out: b(18.6), symbol: "doc.richtext", thumb: "assets/icons/pdf.png", thumbFit: true,
      runTitle: T("job.running", T("job.convert_label", "PDF")), runSub: "Travel Plan.docx",
      doneTitle: T("job.done", T("job.convert_label", "PDF")), doneSub: "Travel Plan.pdf · 182 KB",
      buttons: [{ title: T("card.reveal"), symbol: "folder" }, { title: T("card.open"), symbol: "arrow.up.forward.app" }, { title: T("card.copy"), symbol: "doc.on.clipboard" }, { title: T("card.undo"), symbol: "arrow.uturn.backward" }] })),
    UI.Card(over, Object.assign({}, cardAt, { in: b(31.0), done: b(31.5), out: b(34.6), symbol: "scissors", thumb: "assets/shiba-cutout.png",
      runTitle: T("job.running", T("tool.removebg")), runSub: "Momo.jpg",
      doneTitle: T("job.done", T("tool.removebg")), doneSub: T("job.in_clipboard"),
      buttons: [{ title: T("card.save"), symbol: "square.and.arrow.down" }, { title: T("card.undo"), symbol: "arrow.uturn.backward" }] })),
  ];
  sfx("card", b(6.25)); sfx("done", b(7.25)); sfx("card", b(14.75)); sfx("done", b(15.5)); sfx("card", b(31.0)); sfx("done", b(31.5));
  [b(6.25), b(6.5), b(6.75), b(14.75)].forEach((t, i) => sfx("pop", t, { i }));
  sfx("swish", b(24.02)); sfx("lift", b(31.0));
  sfx("rise", b(2.5)); sfx("whip", b(11.0)); sfx("whip", b(19.25)); sfx("whip", b(35.2));

  // Cut-out: lifts off the photo, floats, then lands in the chat as a sticker.
  const cut = el("div", { class: "abs cutout" }, over);
  el("img", { src: "assets/shiba-cutout.png" }, cut);
  const chat = UI.Chat(over, { w: LY.chat.w, h: LY.chat.h, sw: LY.chat.sw, sh: LY.chat.sh, sy: LY.chat.sy,
    name: C.chatName, initial: C.chatInitial, msg: C.chatMsg, sticker: "assets/shiba-cutout.png", sent: C.sent });
  sfx("slide", b(32.0)); sfx("message", b(32.35)); sfx("paste", b(33.0)); sfx("land", b(33.5)); sfx("sent", b(33.75)); sfx("slide_out", b(35.25));
  blur(b(32.95), b(33.6), 8);

  // Phone + the PDF page that flies to it.
  const phone = UI.Phone(over, { date: C.date, bannerText: C.airdrop, viewerBg: "#F2F2F7" });
  const page = el("div", { class: "docpage" }, over,
    '<div class="dp-photo" style="background-image:url(assets/lisbon.jpg)"></div><div class="dp-title">' + C.docTitle + '</div><div class="dp-sub">' + C.docSub + "</div>" +
    C.docRows.map(([d, s]) => '<div class="dp-row"><b>' + d + "</b>" + s + "</div>").join(""));
  sfx("phone_in", b(38.5)); sfx("fly", b(39.0)); sfx("receive", b(40.0));
  blur(b(38.45), b(39.85), 8);

  // Captions: words rise out of their own masks.
  function Caption(h, s) {
    const node = el("div", { class: "cap" }, over);
    const hEl = el("div", { class: "cap-h" }, node);
    const words = h.split(" ").map((w, i) => {
      if (i) hEl.appendChild(document.createTextNode(" "));
      const m = el("span", { class: "w" }, hEl);
      return el("span", {}, m, w);
    });
    const sEl = el("div", { class: "cap-s" }, node, s);
    set(node, { top: LY.capY + "px" });
    return { node, words, sEl };
  }
  const caps = [["c1", b(4.0), b(10.75)], ["c2", b(12.0), b(18.75)], ["c3", b(20.0), b(27.25)], ["c4", b(28.0), b(35.0)], ["c5", b(36.0), b(40.6)]]
    .map(([k, a, z]) => Object.assign(Caption(C[k + "h"], C[k + "s"]), { a, z }));

  // ------------------------------------------------------------------ hook
  const hook1 = el("div", { class: "big" }, titles, '<span class="mask"><span>' + C.hook1 + "</span></span>");
  const hook2 = el("div", { class: "big accent" }, titles, '<span class="mask"><span>' + C.hook2 + "</span></span>");
  set(hook1, { top: LY.hookY[0] + "px" }); set(hook2, { top: LY.hookY[1] + "px" });
  const bigKey = UI.Keycap(titles, "⌥", "option", true);
  sfx("slam", 0); sfx("slam", b(1)); sfx("keypop", b(1.5)); sfx("key", b(2.5), { big: true }); sfx("key", b(3.5));

  // ------------------------------------------------------------------ montage
  const PH = LY.phone;
  const FEATURES = ["trim", "merge", "faces", "lock", "shrink", "ocr"];
  const M0T = b(42), MSTEP = b(2), MN = FEATURES.length;
  const montage = PV.Montage(montL, {
    W, H, t0: M0T, step: MSTEP, wipe: { t0: b(41.0), t1: b(42.0), x: PH[0], y: PH[1] }, end: b(54.3), implode: b(54.5),
    features: FEATURES, words: C.words, more: C.more, copied: C.copied,
    ring: V ? { cx: 540, cy: 1060, R: 372, tile: 164 } : { cx: 1320, cy: 560, R: 330, tile: 148 },
    disc: V ? { cx: 540, cy: 1060, r: 222 } : { cx: 1320, cy: 560, r: 204 },
    text: V ? { x: 0, y: 246, gap: 54, align: "center", size: 116 } : { x: 150, y: 404, gap: 50, align: "left", size: 104 },
  });
  sfx("wipe", b(41.0));
  for (let i = 0; i < MN; i++) {
    sfx("tile", M0T + i * MSTEP, { i });
    sfx("v_" + FEATURES[i], M0T + i * MSTEP + 0.12, { i });
    blur(M0T + i * MSTEP - 0.02, M0T + i * MSTEP + 0.3, 6);
  }
  sfx("implode", b(54.5));
  blur(b(54.45), b(55.4), 10);
  blur(b(40.95), b(42.05), 6);

  // ------------------------------------------------------------------ finale
  const finSvg = el("svg", { class: "abs fin-svg", width: W, height: H, viewBox: "0 0 " + W + " " + H }, fin);
  el("rect", { x: -100000, y: -100000, width: 200000, height: 200000, fill: "#EDEDF0" }, finSvg);
  const mark = PV.Icon(finSvg, 1024, "fin");
  const name = el("div", { class: "name" }, fin, '<span class="mask"><span>Pickverter</span></span>');
  const byline = el("div", { class: "byline" }, fin, "by <b>ivLis STUDIO</b>");
  const foot = el("div", { class: "foot" }, fin, C.foot);
  const FIN0 = b(56), FP = [650, 347], Z0 = 70;
  sfx("zoomout", FIN0); sfx("impact", FIN0); sfx("click", b(57.5), { icon: true }); sfx("shing", b(57.55)); sfx("name", b(57.75)); sfx("chime", b(58.25));
  // The first frames of the fly-out change scale fastest: more sub-frames there so edges smear instead of stepping.
  blur(FIN0 - 0.02, FIN0 + 0.2, 40); blur(FIN0 + 0.2, FIN0 + 0.75, 16); blur(b(56.9), b(57.6), 6); blur(b(57.7), b(58.4), 6);

  window.MOTION_BLUR = BLUR;
  window.MUSIC = { bpm: BPM, beats: 66 };

  // ------------------------------------------------------------------ render
  function renderAt(t) {
    const cam = camAt(t);
    const worldOn = t >= b(2.4) && t < b(42.2);
    set(world, { display: worldOn ? "block" : "none", transform: "translate(" + W / 2 + "px," + H / 2 + "px) scale(" + cam.z + ") translate(" + -cam.x + "px," + -cam.y + "px)" });
    set(over, { display: worldOn ? "block" : "none" });

    // Finder
    finder.update({
      sel: selAt(t), marquee: marqueeAt(t),
      appear: { "IMG_2041.jpg": spring(t - b(6.25), 0.42, 0.6), "IMG_2042.jpg": spring(t - b(6.5), 0.42, 0.6), "IMG_2043.jpg": spring(t - b(6.75), 0.42, 0.6),
        "Travel Plan.pdf": spring(t - b(14.75), 0.42, 0.6) },
      rotate: { "Momo.jpg": -90 + 90 * spring(t - b(24.02), 0.5, 0.62) },
    });

    // cursor
    const [cx, cy] = cursorAt(t);
    let press = 0, ring = 0;
    for (const c of clicks) {
      press = Math.max(press, prog(t, c - 0.05, c) * (1 - prog(t, c + 0.02, c + 0.14)));
      if (t >= c && t < c + 0.45) ring = prog(t, c, c + 0.45);
    }
    const dragging = t >= b(3.0) && t < b(3.42) ? 0.6 : 0;
    const cOp = E.outCubic(prog(t, b(2.8), b(2.95))) * (1 - prog(t, b(38.6), b(38.95)));
    cursor.update({ x: cx, y: cy, press: Math.max(press, dragging), ring, opacity: cOp });

    for (const w of wheels) w.update(t);

    // AirDrop pulses
    PULSE.forEach((tp, i) => {
      const u = prog(t, tp, tp + 0.9);
      const r = pulseRings[i];
      attr(r, "r", lerp(46, 300, E.outCubic(u)));
      attr(r, "stroke-width", lerp(7, 1.5, u));
      attr(r, "opacity", u > 0 && u < 1 ? (1 - u) * 0.75 : 0);
    });

    // keycaps (screen space)
    for (const kc of keycaps) {
      const n = kc.g.keys.length;
      const visible = t >= kc.show - 0.01 && t <= kc.g.hide + 0.3;
      const second = n > 1 ? kc.g.keys[1][1] : Infinity;
      const spread = n > 1 ? spring(t - second, 0.3, 0.82) : 0;
      const x = W / 2 + (n > 1 ? (kc.i === 0 ? -60 : 60) * (kc.i === 0 ? spread : 1) : 0);
      const pop = visible ? spring(t - kc.show, 0.3, 0.7) * (1 - E.inCubic(prog(t, kc.g.hide, kc.g.hide + 0.22))) : 0;
      const pr = prog(t, kc.press - 0.04, kc.press) * (1 - prog(t, kc.press + 0.08, kc.press + 0.18));
      kc.cap.update({ x, y: LY.keyY, show: pop, press: pr });
    }

    for (const c of cards) c.update(t);

    // cut-out
    {
      const a = spring(t - b(31.0), 0.55, 0.74);
      const f = E.inOutCubic(prog(t, b(33.0), b(33.5)));
      const vis = t >= b(31.0) && t < b(33.5);
      set(cut, { display: vis ? "block" : "none" });
      if (vis) {
        const [px, py, z] = toScreen(CM, t);
        const ph = 87 * K * z, pw = 58 * K * z;
        const [hx, hy, hh] = LY.hero;
        const hw = (hh * 1066) / 1600;
        const bob = Math.sin((t - b(31)) * 2.6) * 7 * clamp(a);
        let x = lerp(px - pw / 2, hx - hw / 2, a), y = lerp(py - ph / 2, hy - hh / 2, a) - bob, w = lerp(pw, hw, a), h = lerp(ph, hh, a);
        if (f > 0) {
          const s = chat.stickerRect();
          const tx = LY.chat.x + s.x, ty = LY.chat.y + s.y;
          x = lerp(x, tx, f); y = lerp(y, ty - Math.sin(Math.PI * f) * 40, f); w = lerp(w, s.w, f); h = lerp(h, s.h, f);
        }
        set(cut, { transform: "translate(" + x + "px," + y + "px)", width: w + "px", height: h + "px", opacity: clamp(a * 4) });
      }
    }

    // chat
    {
      const show = spring(t - b(32.0), 0.45, 0.8);
      const out = E.inCubic(prog(t, b(35.25), b(35.6)));
      chat.update({ x: LY.chat.x, y: LY.chat.y, from: V ? "y" : "x", show: t >= b(32.0) ? show : 0, out,
        incoming: spring(t - b(32.35), 0.35, 0.62), sticker: t >= b(33.5), sent: E.outCubic(prog(t, b(33.75), b(34.0))) });
    }

    // phone + the flying PDF page
    {
      const s = t >= b(38.5) ? spring(t - b(38.5), 0.5, 0.8) : 0;
      const [fx, fy] = LY.phoneFrom;
      const pxy = [PH[0] + fx * (1 - s), PH[1] + fy * (1 - s)];
      const ps_ = LY.phoneScale;
      phone.update({ x: pxy[0], y: pxy[1], scale: ps_, show: s > 0.001 ? 1 : 0, viewer: E.outCubic(prog(t, b(39.55), b(39.8))), banner: t >= b(40.0) ? spring(t - b(40.0), 0.4, 0.72) : 0 });
      const f = E.inOutCubic(prog(t, b(39.0), b(39.7)));
      set(page, { display: t >= b(39.0) && t < b(42.2) ? "block" : "none" });
      if (t >= b(39.0)) {
        const [sx, sy, z] = toScreen(CP, t);
        const w0 = 62 * K * z;
        const pr = phone.pageRect(pxy[0], pxy[1], ps_);
        const bx = pr.x + pr.w / 2, by = pr.y + pr.h / 2;
        const cxp = (sx + bx) / 2, cyp = Math.min(sy, by) - (V ? 260 : 240);
        const qx = (1 - f) * (1 - f) * sx + 2 * (1 - f) * f * cxp + f * f * bx;
        const qy = (1 - f) * (1 - f) * sy + 2 * (1 - f) * f * cyp + f * f * by;
        const w = lerp(w0, pr.w, f), sc = w / 240, h = 340 * sc;
        set(page, { transform: "translate(" + (qx - w / 2) + "px," + (qy - h / 2) + "px) rotate(" + Math.sin(Math.PI * f) * -7 + "deg) scale(" + sc + ")" });
      }
    }

    // captions
    for (const c of caps) {
      const vis = !window.CLEAN && t >= c.a && t <= c.z + 0.45;
      set(c.node, { display: vis ? "block" : "none" });
      if (!vis) continue;
      c.words.forEach((w, k) => {
        const a = E.outExpo(prog(t, c.a + 0.035 * k, c.a + 0.035 * k + 0.5));
        const o = E.inCubic(prog(t, c.z + 0.025 * k, c.z + 0.025 * k + 0.24));
        set(w, { transform: "translateY(" + ((1 - a) * 108 - o * 108) + "%)" });
      });
      const s = E.outCubic(prog(t, c.a + 0.15, c.a + 0.5)) * (1 - E.inCubic(prog(t, c.z, c.z + 0.2)));
      set(c.sEl, { opacity: s, transform: "translateY(" + (1 - s) * 10 + "px)" });
    }

    // hook
    if (t < b(3.4)) {
      set(titles, { display: "block" });
      set(hook1, { display: "block" }); set(hook2, { display: "block" });
      const l1 = E.outExpo(prog(t, 0, 0.5)), l2 = E.outExpo(prog(t, b(1), b(1) + 0.5));
      const o1 = E.inCubic(prog(t, b(2.55), b(2.55) + 0.26)), o2 = E.inCubic(prog(t, b(2.62), b(2.62) + 0.26));
      set(hook1.firstChild.firstChild, { transform: "translateY(" + ((1 - l1) * 110 - o1 * 110) + "%)" });
      set(hook2.firstChild.firstChild, { transform: "translateY(" + ((1 - l2) * 110 - o2 * 110) + "%)" });
      // the big option key: pops, gets pressed, then shrinks into the keycap slot below the action
      const pop = spring(t - b(1.5), 0.34, 0.62);
      const mv = E.inOutCubic(prog(t, b(2.62), b(3.15)));
      const pr = prog(t, b(2.5) - 0.04, b(2.5)) * (1 - prog(t, b(2.5) + 0.09, b(2.5) + 0.2));
      bigKey.update({ x: W / 2, y: lerp(LY.hookKey, LY.keyY, mv), show: t >= b(1.5) ? pop : 0, press: pr, scale: lerp(1, 96 / 168, mv) });
    } else {
      // the big key lives on as the small one until the wheel opens
      const pr = prog(t, b(3.5) - 0.04, b(3.5)) * (1 - prog(t, b(3.5) + 0.08, b(3.5) + 0.18));
      const hide = E.inCubic(prog(t, b(4.25), b(4.5)));
      set(titles, { display: hide < 1 ? "block" : "none" });
      set(hook1, { display: "none" }); set(hook2, { display: "none" });
      bigKey.update({ x: W / 2, y: LY.keyY, show: 1 - hide, press: pr, scale: 96 / 168 });
    }

    montage.update(t);

    // finale: fly out of the picked slot into the app icon
    {
      const on = t >= FIN0;
      set(fin, { display: on ? "block" : "none" });
      if (on) {
        const e = E.outCubic(prog(t, FIN0, FIN0 + 0.66));
        const z = Math.pow(Z0, 1 - e);
        const I = LY.icon;
        const s = (I.s / 1024) * z;
        const pf = [I.x + ((FP[0] - 512) * I.s) / 1024, I.y + ((FP[1] - 512) * I.s) / 1024];
        const ps = [lerp(W / 2, pf[0], e), lerp(H / 2, pf[1], e)];
        attr(finSvg, "viewBox", FP[0] - ps[0] / s + " " + (FP[1] - ps[1] / s) + " " + W / s + " " + H / s);
        const curIn = E.outCubic(prog(t, b(56.85), b(57.4)));
        const press = prog(t, b(57.42), b(57.5)) * (1 - prog(t, b(57.6), b(57.85)));
        const popAmt = t >= b(57.5) ? spring(t - b(57.5), 0.36, 0.5) : 0;
        mark.update({ m: 1, tile: 1, lit: 1, pop: popAmt, cursor: curIn, press, doc: 1, spin: 0, shadow: clamp(prog(e, 0.8, 1)) });
        const n = E.outExpo(prog(t, b(57.75), b(57.75) + 0.6));
        set(name, { top: LY.nameY + "px", display: t >= b(57.7) ? "block" : "none", letterSpacing: lerp(0.05, -0.02, E.outCubic(prog(t, b(57.75), b(57.75) + 0.8))) + "em" });
        set(name.firstChild.firstChild, { transform: "translateY(" + (1 - n) * 105 + "%)" });
        const by = E.outCubic(prog(t, b(58.25), b(58.25) + 0.5));
        set(byline, { top: LY.byY + "px", opacity: by, transform: "translateY(" + (1 - by) * 14 + "px)" });
        const ft = E.outCubic(prog(t, b(58.75), b(58.75) + 0.5));
        set(foot, { top: LY.footY + "px", opacity: ft });
        // the end card keeps breathing: a slow push, and a nudge on the last hit
        const push = 1 + 0.03 * E.inOutSine(prog(t, b(58), b(66)));
        const nudge = t >= b(64) ? 1 + 0.022 * Math.sin(Math.PI * prog(t, b(64), b(64) + 0.3)) : 1;
        set(fin, { transform: "scale(" + push * nudge + ")", transformOrigin: W / 2 + "px " + (LY.icon.y + LY.icon.s * 0.45) + "px" });
      }
    }
  }

  window.DURATION = DURATION;
  window.renderAt = renderAt;
  window.AUDIO_EVENTS = AUDIO.sort((a, c) => a.t - c.t);
  document.documentElement.lang = LANG === "ko" ? "ko" : "en";
  const faces = ["400 20px 'PV SF'", "500 20px 'PV SF'", "600 20px 'PV SF'", "700 20px 'PV SF'", "800 20px 'PV SF'", "400 20px 'PV SF Mono'", "500 20px 'PV SF Mono'", "700 20px 'PV SF Mono'"].map((f) => document.fonts.load(f));
  const pics = ["assets/lisbon.jpg", "assets/beach.jpg", "assets/latte.jpg", "assets/shiba.jpg", "assets/shiba-cutout.png", "assets/icons/docx.png", "assets/icons/pdf.png"];
  window.ready = Promise.all([...faces, document.fonts.ready, ...[...document.images].map((i) => (i.complete ? 0 : new Promise((r) => (i.onload = i.onerror = r))))])
    .then(() => Promise.all(pics.map((src) => { const im = new Image(); im.src = src; return im.decode().catch(() => 0); })))
    .then(() => { (PV.afterFonts || []).forEach((fn) => fn()); renderAt(+(params.get("t") || 0)); return true; });
})();

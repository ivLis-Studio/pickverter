// Pickverter promo, Windows cut — 35 s at 128 BPM, 16:9 (1920×1080) or 9:16 (1080×1920, ?aspect=v).
// Same beat grid, score and story as the Mac cut (src/scene.js), on Windows 11: File Explorer, Alt, the Windows wheel,
// result cards in the bottom-right corner, and the share sheet sending a PDF to an Android phone through Phone Link.
//   bar 0 hook · 1–3 WebP → JPG · 3–5 Word → PDF · 5–7 rotate · 7–9¾ cut-out → chat · 9¾–12¼ share to the phone
//   12½–15 montage (six kinds of files, two beats each) · 16–17 icon, name · 18 last hit
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
  const DURATION = b(74);

  // ------------------------------------------------------------------ copy
  const I18N = window.I18N || { languages: ["en"], strings: {} };
  const li = Math.max(0, I18N.languages.indexOf(LANG));
  const COPY = {
    en: {
      hook1: "Every file.", hook2: "One key away.",
      c1h: "WebP images to JPG, all at once.", c1s: "A whole batch in one go.",
      c2h: "Word to PDF, just like that.", c2s: "PDF, Word, Excel and PowerPoint too.",
      c3h: "Sideways? Just point the way.", c3s: "Hover a tool and its options fan out.",
      c4h: "Cut it out. Paste it anywhere.", c4s: "Save a file or just copy it, right there.",
      c5h: "Straight to your phone.", c5s: "Share, move and open, right from File Explorer.",
      more: "And so much more",
      words: ["Trim video", "Trim MP3s", "Merge PDFs", "Office to PDF", "Blur faces", "Shrink files"],
      chips: [["Make GIFs", "Extract audio", "Compress video", "Change speed"], ["Convert audio", "Match loudness", "Trim silence", "Split tracks"],
        ["Split pages", "Add a password", "Reorder pages", "PDF to images"], ["Excel to CSV", "PDF to Word", "Markdown & HTML", "Make EPUBs"],
        ["Remove backgrounds", "Resize", "HEIC to JPG", "Collages"], ["Zip", "Grab text", "Copy paths", "Strip location"]],
      copied: "Copied", chatName: "Mina", chatInitial: "M", chatStatus: "Active now", chatMsg: "Send me a pic of Momo!", chatField: "Type a message", sent: "Sent",
      x: { desktop: "Desktop", search: "Search Desktop", new: "New", sort: "Sort", view: "View", home: "Home", gallery: "Gallery", downloads: "Downloads",
        documents: "Documents", pictures: "Pictures", music: "Music", videos: "Videos", pc: "This PC", details: "Details" },
      items: (n) => n + " items", selected: (n, size) => n + (n === 1 ? " item" : " items") + " selected  " + size,
      share: { file: "Travel Plan.pdf", size: "PDF Document · 182 KB", nearby: "Nearby sharing", device: "Mina's laptop", using: "Share using",
        phoneLink: "Phone Link", mail: "Mail", more: "Find more apps" },
      phone: { clock: "10:24", date: "Thu, Oct 22", app: "Link to Windows", now: "now", file: "Travel Plan.pdf", received: "Received from your PC", open: "Open", share: "Share" },
      docTitle: "Lisbon Trip", docSub: "Oct 24 – 27", docRows: [["Day 1", "Alfama walk"], ["Day 2", "Belém & pastéis"], ["Day 3", "Cascais beach"]],
      foot: "Free for Windows  ·  github.com/ivLis-Studio/pickverter",
    },
    ko: {
      hook1: "모든 파일을,", hook2: "키 하나로.",
      c1h: "WebP 이미지, JPG로 한 번에.", c1s: "여러 장도 한꺼번에.",
      c2h: "Word 문서도 PDF로 바로.", c2s: "PDF·Word·Excel·PowerPoint까지.",
      c3h: "누운 사진은 방향만 쓱.", c3s: "툴에 올려 두면 옵션이 펼쳐져요.",
      c4h: "배경은 지우고, 바로 붙여넣기.", c4s: "파일로 저장할지 복사할지, 그 자리에서.",
      c5h: "휴대폰으로 바로 보내기.", c5s: "공유·이동·열기까지 파일 탐색기에서 바로.",
      more: "그리고 더 많이",
      words: ["동영상 자르기", "MP3 자르기", "PDF 합치기", "문서를 PDF로", "얼굴 가리기", "용량 줄이기"],
      chips: [["GIF 만들기", "소리 추출", "동영상 압축", "속도 조절"], ["오디오 변환", "음량 맞추기", "무음 자르기", "구간 나누기"],
        ["페이지 나누기", "암호 걸기", "페이지 정리", "PDF → 이미지"], ["Excel → CSV", "PDF → Word", "Markdown·HTML", "EPUB 만들기"],
        ["배경 제거", "크기 조절", "HEIC → JPG", "콜라주"], ["ZIP 압축", "글자 추출", "경로 복사", "위치 정보 지우기"]],
      copied: "복사됨", chatName: "민아", chatInitial: "민", chatStatus: "활동 중", chatMsg: "모모 사진 보내줘!", chatField: "메시지 입력", sent: "전송됨",
      x: { desktop: "바탕 화면", search: "바탕 화면 검색", new: "새로 만들기", sort: "정렬", view: "보기", home: "홈", gallery: "갤러리", downloads: "다운로드",
        documents: "문서", pictures: "사진", music: "음악", videos: "동영상", pc: "내 PC", details: "자세히" },
      items: (n) => n + "개 항목", selected: (n, size) => n + "개 항목 선택함  " + size.replace(" ", ""),
      share: { file: "Travel Plan.pdf", size: "PDF 문서 · 182KB", nearby: "근거리 공유", device: "민아의 노트북", using: "다음을 사용하여 공유",
        phoneLink: "휴대폰과 연결", mail: "메일", more: "더 많은 앱 찾기" },
      phone: { clock: "10:24", date: "10월 22일 목요일", app: "Windows와 연결", now: "지금", file: "Travel Plan.pdf", received: "PC에서 받은 파일", open: "열기", share: "공유" },
      docTitle: "리스본 여행", docSub: "10월 24일 – 27일", docRows: [["1일차", "알파마 골목 산책"], ["2일차", "벨렝 탑 · 에그타르트"], ["3일차", "카스카이스 해변"]],
      foot: "Windows용 무료 앱  ·  github.com/ivLis-Studio/pickverter",
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

  // Layout per aspect. Screen-space anchors; the camera frames world points onto them. File Explorer's chrome is taller
  // than Finder's, so the wheel sits a little lower than in the Mac cut and the window clears the captions.
  const CW = 368 * K;
  const LY = V ? {
    wheel: [540, 990], z: 1.55, keyY: 1560, capY: 232, hookY: [640, 784], hookKey: 1110,
    card: { x: (1080 - CW) / 2, top: 520, origin: "50% 0" },
    hero: [540, 860, 400], chat: { x: 110, y: 1110, w: 860, h: 400, sw: 116, sh: 174, sy: 78 },
    phone: [790, 1190], phoneFrom: [0, 1000], phoneScale: 1.28,
    icon: { x: 540, y: 800, s: 560 }, nameY: 1080, byY: 1246, footY: 1342,
  } : {
    wheel: [945, 520], z: 1.12, keyY: 990, capY: 66, hookY: [292, 436], hookKey: 770,
    card: { x: 1920 - 40 - CW, bottom: 40, origin: "100% 100%" },
    hero: [700, 560, 470], chat: { x: 1160, y: 230, w: 480, h: 600, sw: 150, sh: 225, sy: 104 },
    phone: [1560, 560], phoneFrom: [580, 0], phoneScale: 1.12,
    icon: { x: 960, y: 392, s: 410 }, nameY: 636, byY: 800, footY: 990,
  };

  // ------------------------------------------------------------------ data
  const FILES = [
    { name: "tram.webp", photo: "assets/lisbon.jpg", w: 92, h: 61, slot: 0 },
    { name: "beach.webp", photo: "assets/beach.jpg", w: 92, h: 61, slot: 1 },
    { name: "latte.webp", photo: "assets/latte.jpg", w: 92, h: 61, slot: 2 },
    { name: "Momo.jpg", photo: "assets/shiba.jpg", w: 58, h: 87, slot: 3 },
    { name: "Travel Plan.docx", kind: "docx", slot: 4 },
    { name: "tram.jpg", photo: "assets/lisbon.jpg", w: 92, h: 61, slot: 5 },
    { name: "beach.jpg", photo: "assets/beach.jpg", w: 92, h: 61, slot: 6 },
    { name: "latte.jpg", photo: "assets/latte.jpg", w: 92, h: 61, slot: 7 },
    { name: "Trip.mp4", photo: "assets/beach.jpg", w: 92, h: 52, video: true, slot: 8 },
    { name: "Travel Plan.pdf", kind: "pdf", slot: 9 },
  ];
  // The rings the Windows app shows for these files (Catalog.cs), in its order; the Actions ring is tidied for everyday use
  // (slots can be hidden and moved in Settings).
  const fmtRing = (list) => list.map((title) => ({ title, format: true, files: true }));
  const v = (title, symbol, d) => ({ title, symbol, default: !!d });
  const dpi = () => [v("300 dpi", "", 1), v("150 dpi", ""), v("600 dpi", "")];
  const folders = () => [v(T("folder.choose"), "folder", 1), v(T("folder.desktop"), "menubar.dock.rectangle"), v(T("folder.downloads"), "arrow.down.circle")];
  const RINGS = {
    convertWebp: fmtRing(["JPG", "PNG", "AVIF", "HEIC", "TIFF", "GIF", "BMP", "PDF", "ICO"]),
    convertJpg: fmtRing(["PNG", "WebP", "AVIF", "HEIC", "TIFF", "GIF", "BMP", "PDF", "ICO"]),
    convertDocx: fmtRing(["PDF", "RTF", "TXT", "MD", "HTML", "ODT", "EPUB"]),
    convertPdf: [{ title: "PNG", format: true, files: true, variants: dpi() }, { title: "JPG", format: true, files: true, variants: dpi() }, ...fmtRing(["TXT", "MD", "DOCX", "RTF", "HTML"])],
    tools: [
      { title: T("tool.compress"), symbol: "arrow.down.right.and.arrow.up.left", files: true, variants: [v(T("q.balanced"), "", 1), v(T("q.high"), ""), v(T("q.small"), "")] },
      { title: T("tool.resize"), symbol: "arrow.down.forward.and.arrow.up.backward", files: true, variants: [v("50%", "", 1), v("25%", ""), v("75%", ""), v("1280px", ""), v("1920px", ""), v("3840px", "")] },
      { title: T("tool.crop"), symbol: "crop", files: true, variants: [v(T("crop.custom"), "crop", 1), v(T("crop.center", "1:1"), ""), v(T("crop.center", "16:9"), ""), v(T("crop.center", "9:16"), ""), v(T("crop.center", "4:5"), "")] },
      { title: T("tool.removebg"), symbol: "scissors", files: true, variants: [v(T("bg.transparent"), "", 1), v(T("bg.crop"), ""), v(T("bg.white"), "")] },
      { title: T("tool.redact"), symbol: "checkerboard.rectangle", files: true, variants: [v(T("redact.custom"), "", 1), v(T("redact.auto_faces"), ""), v(T("redact.auto_text"), "")] },
      { title: T("tool.ocr"), symbol: "text.viewfinder", detail: T("tool.ocr.d") },
      { title: T("tool.rotate"), symbol: "rotate.right", files: true, variants: [v(T("rot.right"), "rotate.right", 1), v(T("rot.left"), "rotate.left"), v(T("rot.half"), "arrow.triangle.2.circlepath"), v(T("rot.flipH"), "flip.horizontal"), v(T("rot.flipV"), "flip.vertical")] },
      { title: T("tool.meta"), symbol: "info.circle", variants: [v(T("meta.edit"), "pencil", 1), v(T("strip.all"), "eye.slash"), v(T("strip.gps"), "location.slash")] },
      { title: T("tool.zip"), symbol: "archivebox", files: true, detail: T("tool.zip.d") },
    ],
    actions: [
      { title: T("action.share"), symbol: "square.and.arrow.up", detail: T("action.share.d") },
      { title: T("action.copyfile"), symbol: "doc.on.doc", detail: T("action.copyfile.d") },
      { title: T("action.move"), symbol: "folder", variants: folders() },
      { title: T("action.duplicate"), symbol: "plus.square.on.square", variants: folders() },
      { title: T("action.float"), symbol: "pin", detail: T("action.float.d") },
      { title: T("action.openwith"), symbol: "apps.list", detail: T("action.openwith.d") },
      { title: T("action.copypath"), symbol: "link", variants: [v(T("path.full"), "link", 1), v(T("path.quoted"), "link"), v(T("path.name"), "link"), v(T("path.unix"), "link")] },
    ],
    choice: [
      { title: T("dest.file"), symbol: "doc" },
      { title: T("dest.clipboard"), symbol: "doc.on.clipboard" },
      { title: T("dest.both"), symbol: "doc.on.doc" },
    ],
  };

  // ------------------------------------------------------------------ world: File Explorer
  const WX = 336, WY = 262;
  const explorer = UI.Explorer(world, FILES, WX, WY, C.x);
  const at = (n) => explorer.at(n);
  const C42 = at("beach.webp"), CD = at("Travel Plan.docx"), CP = at("Travel Plan.pdf"), CM = at("Momo.jpg");
  const slot = (c, n, i, r = 106) => polar(c[0], c[1], r * K, -90 + (360 / n) * i);
  const SUB_R = (G.subInner + G.subOuter) / 2;
  const toLocal = (p) => [(p[0] - WX) / K, (p[1] - WY) / K];
  const rowMid = (at("beach.webp")[1] + at("beach.jpg")[1]) / 2;

  // The share sheet sits where the wheel was (world space), so the camera and the cursor treat it like the window it is.
  const SH = UI.SHEET;
  const sheetAt = [CP[0] - (SH.w * K) / 2, CP[1] - 252 * K];
  const sheetPt = (p) => [sheetAt[0] + p[0] * K, sheetAt[1] + p[1] * K];
  const sheet = UI.ShareSheet(world, C.share);

  // ------------------------------------------------------------------ camera
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
  const res1 = [at("beach.jpg")[0], rowMid], res1A = V ? [540, 1000] : [960, 600], zr1 = V ? 1.2 : 1.0;
  const res2 = [CD[0], rowMid], res2A = V ? [640, 1000] : [1060, 600], zr2 = V ? 1.4 : 1.1;
  const AD = V ? A : [1000, 520];
  const momo = V ? 1.7 : 1.3;
  const AM = V ? A : [A[0], A[1] + 30];
  const AP = V ? [540, 1080] : [860, 650], ZP = V ? 1.4 : 1.0;
  shot(0, frame(C42, [A[0], A[1] + H + 120], Z * 0.92));
  shot(b(2.5), frame(C42, [A[0], A[1] + H + 120], Z * 0.92));
  shot(b(3.35), frame(C42, A, Z), E.outExpo);
  shot(b(4.0), frame(C42, A, Z));
  shot(b(4.6), frame(C42, A, Z * 1.06), E.outCubic);
  shot(b(7.0), frame(C42, A, Z * 1.09), E.linear);
  shot(b(8.0), frame(res1, res1A, zr1), E.inOutCubic);
  shot(b(12.5), frame(res1, res1A, zr1 * 1.05), E.linear);
  shot(b(13.25), frame(CD, AD, Z * 1.04), E.inOutExpo);
  shot(b(17.0), frame(CD, AD, Z * 1.1), E.linear);
  shot(b(18.0), frame(res2, res2A, zr2), E.inOutCubic);
  shot(b(21.25), frame(res2, res2A, zr2 * 1.04), E.linear);
  shot(b(22.0), frame(CM, AM, momo), E.inOutExpo);
  shot(b(27.25), frame(CM, AM, momo * 1.05), E.linear);
  shot(b(29.35), frame(CM, [AM[0], AM[1] + 20], momo * 1.1), E.inOutSine);
  shot(b(30.0), frame(CM, AM, momo), E.outCubic);
  shot(b(34.5), frame(CM, AM, momo * 1.03), E.linear);
  shot(b(35.5), frame(CM, V ? [540, 990] : [700, 580], V ? 1.1 : 1.0), E.inOutCubic);
  shot(b(39.0), frame(CM, V ? [540, 1000] : [690, 590], V ? 1.13 : 1.02), E.linear);
  shot(b(39.75), frame(CP, AP, ZP), E.inOutExpo);
  shot(b(43.0), frame(CP, AP, ZP * 1.04), E.linear);
  shot(b(45.25), frame(CP, V ? [540, 1100] : [880, 660], ZP * 1.06), E.inOutCubic);
  shot(b(46.25), frame(CP, V ? [400, 1000] : [720, 640], V ? 1.1 : 0.96), E.inOutCubic);
  shot(b(50.0), frame(CP, V ? [390, 990] : [700, 640], V ? 1.15 : 1.0), E.linear);
  punch(b(4.0), 0.05); punch(b(17.0), 0.035); punch(b(27.25), 0.04); punch(b(34.75), 0.035); punch(b(43.0), 0.03); punch(b(45.25), 0.035);
  blur(b(2.5), b(3.4), 10); blur(b(3.95), b(4.45), 8); blur(b(12.45), b(13.35), 12); blur(b(16.95), b(18.1), 6);
  blur(b(21.2), b(22.05), 12); blur(b(27.2), b(27.75), 6); blur(b(34.65), b(35.55), 6); blur(b(38.95), b(39.8), 12); blur(b(45.2), b(46.3), 6);

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

  // 1 · marquee over the three WebP images → JPG
  const M0 = explorer.toStage(205, 140), M1 = explorer.toStage(604, 278);
  to(b(2.85), b(3.0), M0, 0.0, E.outCubic);
  to(b(3.0), b(3.42), M1, 0.0, E.inOutSine);
  to(b(3.46), b(3.86), C42, 0.1);
  to(b(5.0), b(5.6), slot(C42, 9, 0), 0.06);
  click(b(7.0));
  to(b(7.15), b(7.9), [C42[0] - 70, C42[1] + 128], 0.12);
  // 2 · Word → PDF
  to(b(12.5), b(13.22), [CD[0] + 6, CD[1] + 8], 0.1, E.inOutExpo);
  click(b(13.5));
  to(b(15.15), b(15.75), slot(CD, 7, 0), 0.06);
  click(b(17.0));
  to(b(17.15), b(17.9), [CD[0] - 150, CD[1] + 96], 0.12);
  // 3 · rotate: hover Rotate, its options fan out, glide to Right 90°
  to(b(21.25), b(21.95), [CM[0] + 6, CM[1] + 8], 0.1, E.inOutExpo);
  click(b(22.25));
  to(b(24.1), b(24.7), slot(CM, 9, 6), 0.06);
  to(b(25.1), b(25.5), polar(CM[0], CM[1], SUB_R * K, 150), 0.0);
  arc(b(25.7), b(26.5), CM, SUB_R, 150, 86);
  click(b(27.25));
  to(b(27.45), b(28.15), [CM[0] + 120, CM[1] + 150], 0.12);
  // 4 · remove background → clipboard
  to(b(29.0), b(29.45), CM, 0.1);
  to(b(30.85), b(31.45), slot(CM, 9, 3), 0.06);
  click(b(32.5));
  arc(b(32.75), b(33.25), CM, 106, 30, -82);
  arc(b(33.5), b(34.0), CM, 106, -82, 26);
  click(b(34.5));
  to(b(34.7), b(35.35), [CM[0] + 150, CM[1] + 190], 0.12);
  // 5 · Share → Phone Link
  const PL = sheetPt(sheet.tileCenter(1));
  to(b(39.0), b(39.72), [CP[0] + 6, CP[1] + 8], 0.1, E.inOutExpo);
  click(b(40.0));
  to(b(41.75), b(42.35), slot(CP, 7, 0), 0.06);
  click(b(43.0));
  to(b(43.6), b(44.2), [PL[0] + 4, PL[1] + 6], 0.1);
  click(b(45.25));
  to(b(45.4), b(45.95), [PL[0] + 60, PL[1] + 130], 0.12);

  const WEBPS = ["tram.webp", "beach.webp", "latte.webp"];
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
      for (const n of WEBPS) { const bx = explorer.box(n); sel[n] = bx.l < r && bx.r > l && bx.t < bt && bx.b > tp ? 1 : 0; }
    } else if (t >= b(13.5) && t < b(22.25)) sel["Travel Plan.docx"] = 1;
    else if (t >= b(22.25) && t < b(40.0)) sel["Momo.jpg"] = 1;
    else if (t >= b(40.0)) sel["Travel Plan.pdf"] = 1;
    return sel;
  }
  function statusAt(t, sel) {
    const n = 6 + (t >= b(7.25) ? 1 : 0) + (t >= b(7.5) ? 1 : 0) + (t >= b(7.75) ? 1 : 0) + (t >= b(17.25) ? 1 : 0);
    const picked = Object.keys(sel).filter((k) => sel[k] > 0.5);
    const sizes = { "Travel Plan.docx": "46.2 KB", "Momo.jpg": "2.31 MB", "Travel Plan.pdf": "182 KB" };
    let s = "";
    if (picked.length > 1) s = C.selected(picked.length, (0.61 * picked.length).toFixed(2) + " MB");
    else if (picked.length === 1) s = C.selected(1, sizes[picked[0]] || "612 KB");
    return [C.items(n), s];
  }

  // ------------------------------------------------------------------ wheels
  const files3 = { name: "", thumb: "assets/lisbon.jpg", count: 3 };
  const momoFile = { name: "Momo.jpg", thumb: "assets/shiba.jpg" };
  const wheels = [
    UI.Wheel(world, RINGS, { id: 1, at: C42, open: b(4.0), close: b(7.0), pick: true, bloom: true, shock: true, modes: [{ t: b(4.0), ring: "convertWebp" }], cursor: cursorAt,
      file: files3, preview: (it) => PV.tp("wheel.files_to", 3, it.title) }),
    UI.Wheel(world, RINGS, { id: 2, at: CD, open: b(14.25), close: b(17.0), pick: true, modes: [{ t: b(14.25), ring: "convertDocx" }], cursor: cursorAt,
      file: { name: "Travel Plan.docx", icon: "docx" }, preview: (it) => "Travel Plan." + it.title.toLowerCase() }),
    UI.Wheel(world, RINGS, { id: 3, at: CM, open: b(23.0), close: b(27.25), pick: true, modes: [{ t: b(23.0), ring: "convertJpg" }, { t: b(23.25), ring: "tools" }], cursor: cursorAt,
      file: momoFile, preview: (it) => "Momo." + it.title.toLowerCase() }),
    UI.Wheel(world, RINGS, { id: 4, at: CM, open: b(30.0), close: b(34.5), pick: true, modes: [{ t: b(30.0), ring: "convertJpg" }, { t: b(30.25), ring: "tools" }], choice: { t: b(32.5), ring: "choice" },
      choiceOption: T("tool.removebg"), cursor: cursorAt, file: momoFile, preview: (it) => "Momo." + it.title.toLowerCase() }),
    UI.Wheel(world, RINGS, { id: 5, at: CP, open: b(40.75), close: b(43.0), pick: true, modes: [{ t: b(40.75), ring: "convertPdf" }, { t: b(41.25), ring: "actions" }], cursor: cursorAt,
      file: { name: "Travel Plan.pdf", icon: "pdf" }, preview: (it) => "Travel Plan." + it.title.toLowerCase() }),
  ];
  for (const w of wheels) w.ticks.forEach((t) => sfx("tick", t));
  sfx("bloom", b(4.0));
  [b(14.25), b(23.0), b(30.0), b(40.75)].forEach((t) => sfx("open", t));
  [b(7.0), b(17.0), b(27.25), b(34.5), b(43.0)].forEach((t) => sfx("close", t));
  [b(23.25), b(30.25), b(41.25)].forEach((t) => sfx("swap", t));
  sfx("choice", b(32.5));

  // The share sheet (world space) and its two beats: hover Phone Link, click.
  const SHEET_IN = b(43.1), SHEET_OUT = b(45.5), PL_HOVER = b(44.1), PL_CLICK = b(45.25);
  sfx("card", SHEET_IN); sfx("pop", SHEET_IN + 0.04, { i: 1 }); sfx("tick", PL_HOVER + 0.05);
  const cursor = UI.Cursor(world);

  // ------------------------------------------------------------------ overlays
  const KEY = { alt: [T("key.alt")], ctrl: [T("key.ctrl")], v: ["V", true] };
  const keyGroups = [
    { keys: [["alt", b(13.8), b(14.0)]], hide: b(14.9) },
    { keys: [["alt", b(22.55), b(22.75)], ["ctrl", b(22.85), b(23.25)]], hide: b(24.0) },
    { keys: [["alt", b(29.55), b(29.75)], ["ctrl", b(29.85), b(30.25)]], hide: b(31.0) },
    { keys: [["ctrl", b(36.45), b(36.75)], ["v", b(36.5), b(36.75)]], hide: b(37.35) },
    { keys: [["alt", b(40.3), b(40.5)], ["alt", b(40.85), b(41.1)]], hide: b(41.85) },
  ];
  const keycaps = [];
  for (const g of keyGroups) {
    g.keys.forEach(([k, show, press], i) => {
      const [label, letter] = KEY[k];
      keycaps.push({ cap: UI.Keycap(over, label, { letter: !!letter }), g, i, show, press });
      if (k !== "v") sfx("key", press);
    });
  }

  const cardAt = LY.card;
  const btn = (key, symbol) => ({ title: T(key), symbol });
  const fileButtons = [btn("card.reveal", "folder"), btn("card.open", "arrow.up.forward.app"), btn("card.copy", "doc.on.clipboard"), btn("card.undo", "arrow.uturn.backward")];
  const cardBase = { x: cardAt.x, bottom: cardAt.bottom, top: cardAt.top, origin: cardAt.origin };
  const cards = [
    UI.Card(over, Object.assign({}, cardBase, { in: b(7.25), done: b(8.25), out: b(12.0), symbol: "photo", thumb: "assets/lisbon.jpg",
      runTitle: T("job.running", T("job.convert_label", "JPG")), runSub: PV.tp("wheel.items", 3),
      doneTitle: T("job.done", T("job.convert_label", "JPG")), doneSub: PV.tp("job.files_created", 3) + " · 1.2 MB", buttons: fileButtons })),
    UI.Card(over, Object.assign({}, cardBase, { in: b(17.25), done: b(18.0), out: b(21.0), symbol: "doc.richtext", icon: "pdf",
      runTitle: T("job.running", T("job.convert_label", "PDF")), runSub: "Travel Plan.docx",
      doneTitle: T("job.done", T("job.convert_label", "PDF")), doneSub: "Travel Plan.pdf · 182 KB", buttons: fileButtons })),
    UI.Card(over, Object.assign({}, cardBase, { in: b(34.75), done: b(35.25), out: b(38.25), symbol: "scissors", thumb: "assets/shiba-cutout.png",
      runTitle: T("job.running", T("tool.removebg")), runSub: "Momo.jpg",
      doneTitle: T("job.done", T("tool.removebg")), doneSub: T("job.in_clipboard"),
      buttons: [btn("card.save", "square.and.arrow.down"), btn("card.open", "arrow.up.forward.app")] })),
  ];
  sfx("card", b(7.25)); sfx("done", b(8.25)); sfx("card", b(17.25)); sfx("done", b(18.0)); sfx("card", b(34.75)); sfx("done", b(35.25));
  [b(7.25), b(7.5), b(7.75), b(17.25)].forEach((t, i) => sfx("pop", t, { i }));
  sfx("swish", b(27.27)); sfx("lift", b(34.75));
  sfx("rise", b(2.5)); sfx("whip", b(12.5)); sfx("whip", b(21.25)); sfx("whip", b(39.0));

  // Cut-out: lifts off the photo, floats, then lands in the chat as a sticker.
  const cut = el("div", { class: "abs cutout" }, over);
  el("img", { src: "assets/shiba-cutout.png" }, cut);
  const chat = UI.Chat(over, { w: LY.chat.w, h: LY.chat.h, sw: LY.chat.sw, sh: LY.chat.sh, sy: LY.chat.sy,
    name: C.chatName, initial: C.chatInitial, status: C.chatStatus, msg: C.chatMsg, field: C.chatField, sticker: "assets/shiba-cutout.png", sent: C.sent });
  sfx("slide", b(35.75)); sfx("message", b(36.1)); sfx("paste", b(36.75)); sfx("land", b(37.25)); sfx("sent", b(37.5)); sfx("slide_out", b(38.75));
  blur(b(36.7), b(37.35), 8);

  // Android phone + the PDF page that flies to it out of the share sheet.
  const phone = UI.Phone(over, Object.assign({ thumb: "assets/lisbon.jpg" }, C.phone));
  const page = el("div", { class: "docpage" }, over,
    '<div class="dp-photo" style="background-image:url(assets/lisbon.jpg)"></div><div class="dp-title">' + C.docTitle + '</div><div class="dp-sub">' + C.docSub + "</div>" +
    C.docRows.map(([d, s]) => '<div class="dp-row"><b>' + d + "</b>" + s + "</div>").join(""));
  const PHONE_IN = PL_CLICK, FLY = [b(45.5), b(46.25)], NOTE = b(46.5);
  sfx("phone_in", PHONE_IN); sfx("fly", FLY[0]); sfx("receive", NOTE);
  blur(FLY[0] - 0.05, FLY[1] + 0.1, 8);

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
  const caps = [["c1", b(4.0), b(12.25)], ["c2", b(13.5), b(21.0)], ["c3", b(22.25), b(29.0)], ["c4", b(30.0), b(38.5)], ["c5", b(40.0), b(48.6)]]
    .map(([k, a, z]) => Object.assign(Caption(C[k + "h"], C[k + "s"]), { a, z }));

  // ------------------------------------------------------------------ hook
  const hook1 = el("div", { class: "big" }, titles, '<span class="mask"><span>' + C.hook1 + "</span></span>");
  const hook2 = el("div", { class: "big accent" }, titles, '<span class="mask"><span>' + C.hook2 + "</span></span>");
  set(hook1, { top: LY.hookY[0] + "px" }); set(hook2, { top: LY.hookY[1] + "px" });
  const bigKey = UI.Keycap(titles, T("key.alt"), { big: true });
  sfx("slam", 0); sfx("slam", b(1)); sfx("keypop", b(1.5)); sfx("key", b(2.5), { big: true }); sfx("key", b(3.5));

  // ------------------------------------------------------------------ montage
  const PH = LY.phone;
  const FEATURES = ["trim", "audiotrim", "merge", "docs", "faces", "shrink"];
  const M0T = b(50), MSTEP = b(2), MN = FEATURES.length;
  const montage = PV.Montage(montL, {
    W, H, t0: M0T, step: MSTEP, wipe: { t0: b(49.0), t1: b(50.0), x: PH[0], y: PH[1] }, end: b(62.3), implode: b(62.5),
    features: FEATURES, words: C.words, chips: C.chips, more: C.more, copied: C.copied,
    ring: V ? { cx: 540, cy: 1096, R: 358, tile: 158 } : { cx: 1320, cy: 560, R: 330, tile: 148 },
    disc: V ? { cx: 540, cy: 1096, r: 214 } : { cx: 1320, cy: 560, r: 204 },
    text: V ? { x: 0, y: 236, gap: 54, align: "center", size: 116, chipW: 960, chipGap: 22 } : { x: 150, y: 372, gap: 50, align: "left", size: 104, chipW: 740, chipGap: 22 },
    icon: (name, size, color, parent) => UI.glyphNode(name, size, color, parent), tileRadius: 0.2,
  });
  sfx("wipe", b(49.0));
  for (let i = 0; i < MN; i++) {
    sfx("tile", M0T + i * MSTEP, { i });
    sfx("v_" + FEATURES[i], M0T + i * MSTEP + 0.12, { i });
    blur(M0T + i * MSTEP - 0.02, M0T + i * MSTEP + 0.3, 6);
  }
  sfx("implode", b(62.5));
  blur(b(62.45), b(63.4), 10);
  blur(b(48.95), b(50.05), 6);

  // ------------------------------------------------------------------ finale
  const finSvg = el("svg", { class: "abs fin-svg", width: W, height: H, viewBox: "0 0 " + W + " " + H }, fin);
  el("rect", { x: -100000, y: -100000, width: 200000, height: 200000, fill: "#E9EDF2" }, finSvg);
  const mark = PV.WinIcon(finSvg, 1024, "fin");
  const name = el("div", { class: "name" }, fin, '<span class="mask"><span>Pickverter</span></span>');
  const byline = el("div", { class: "byline" }, fin, "by <b>ivLis STUDIO</b>");
  const foot = el("div", { class: "foot" }, fin, C.foot);
  const FIN0 = b(64), FP = [650, 347], Z0 = 70;
  sfx("zoomout", FIN0); sfx("impact", FIN0); sfx("click", b(65.5), { icon: true }); sfx("shing", b(65.55)); sfx("name", b(65.75)); sfx("chime", b(66.25));
  blur(FIN0 - 0.02, FIN0 + 0.2, 40); blur(FIN0 + 0.2, FIN0 + 0.75, 16); blur(b(64.9), b(65.6), 6); blur(b(65.7), b(66.4), 6);

  window.MOTION_BLUR = BLUR;
  window.MUSIC = { bpm: BPM, beats: 74, shift: 2 };

  // ------------------------------------------------------------------ render
  function renderAt(t) {
    const cam = camAt(t);
    const worldOn = t >= b(2.4) && t < b(50.2);
    set(world, { display: worldOn ? "block" : "none", transform: "translate(" + W / 2 + "px," + H / 2 + "px) scale(" + cam.z + ") translate(" + -cam.x + "px," + -cam.y + "px)" });
    set(over, { display: worldOn ? "block" : "none" });

    const sel = selAt(t);
    explorer.update({
      sel, marquee: marqueeAt(t), status: statusAt(t, sel),
      appear: { "tram.jpg": spring(t - b(7.25), 0.42, 0.6), "beach.jpg": spring(t - b(7.5), 0.42, 0.6), "latte.jpg": spring(t - b(7.75), 0.42, 0.6),
        "Travel Plan.pdf": spring(t - b(17.25), 0.42, 0.6) },
      rotate: { "Momo.jpg": -90 + 90 * spring(t - b(27.27), 0.5, 0.62) },
    });

    const [cx, cy] = cursorAt(t);
    let press = 0, ring = 0;
    for (const c of clicks) {
      press = Math.max(press, prog(t, c - 0.05, c) * (1 - prog(t, c + 0.02, c + 0.14)));
      if (t >= c && t < c + 0.45) ring = prog(t, c, c + 0.45);
    }
    const dragging = t >= b(3.0) && t < b(3.42) ? 0.6 : 0;
    const cOp = E.outCubic(prog(t, b(2.8), b(2.95))) * (1 - prog(t, b(45.55), b(45.9)));
    cursor.update({ x: cx, y: cy, press: Math.max(press, dragging), ring, opacity: cOp });

    for (const w of wheels) w.update(t);

    // share sheet
    {
      const show = t >= SHEET_IN ? spring(t - SHEET_IN, 0.34, 0.8) : 0;
      const out = E.inCubic(prog(t, SHEET_OUT, SHEET_OUT + 0.3));
      const hover = E.outQuad(prog(t, PL_HOVER, PL_HOVER + 0.05));
      const pr = prog(t, PL_CLICK - 0.05, PL_CLICK) * (1 - prog(t, PL_CLICK + 0.05, PL_CLICK + 0.2));
      sheet.update({ x: sheetAt[0], y: sheetAt[1], show, out, hover: { 1: hover }, press: { 1: pr } });
    }

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
      const a = spring(t - b(34.75), 0.55, 0.74);
      const f = E.inOutCubic(prog(t, b(36.75), b(37.25)));
      const vis = t >= b(34.75) && t < b(37.25);
      set(cut, { display: vis ? "block" : "none" });
      if (vis) {
        const [px, py, z] = toScreen(CM, t);
        const ph = 87 * K * z, pw = 58 * K * z;
        const [hx, hy, hh] = LY.hero;
        const hw = (hh * 1066) / 1600;
        const bob = Math.sin((t - b(34.75)) * 2.6) * 7 * clamp(a);
        let x = lerp(px - pw / 2, hx - hw / 2, a), y = lerp(py - ph / 2, hy - hh / 2, a) - bob, w = lerp(pw, hw, a), h = lerp(ph, hh, a);
        if (f > 0) {
          const s = chat.stickerRect();
          const tx = LY.chat.x + s.x, ty = LY.chat.y + s.y;
          x = lerp(x, tx, f); y = lerp(y, ty - Math.sin(Math.PI * f) * 40, f); w = lerp(w, s.w, f); h = lerp(h, s.h, f);
        }
        set(cut, { transform: "translate(" + x + "px," + y + "px)", width: w + "px", height: h + "px", opacity: clamp(a * 4) });
      }
    }

    {
      const show = spring(t - b(35.75), 0.45, 0.8);
      const out = E.inCubic(prog(t, b(38.75), b(39.1)));
      chat.update({ x: LY.chat.x, y: LY.chat.y, from: V ? "y" : "x", show: t >= b(35.75) ? show : 0, out,
        incoming: spring(t - b(36.1), 0.35, 0.62), sticker: t >= b(37.25), sent: E.outCubic(prog(t, b(37.5), b(37.75))) });
    }

    // phone + the flying PDF page
    {
      const s = t >= PHONE_IN ? spring(t - PHONE_IN, 0.5, 0.8) : 0;
      const [fx, fy] = LY.phoneFrom;
      const pxy = [PH[0] + fx * (1 - s), PH[1] + fy * (1 - s)];
      const ps_ = LY.phoneScale;
      phone.update({ x: pxy[0], y: pxy[1], scale: ps_, show: s > 0.001 ? 1 : 0, viewer: E.outCubic(prog(t, FLY[1] - 0.15, FLY[1] + 0.1)), note: t >= NOTE ? spring(t - NOTE, 0.4, 0.72) : 0 });
      const f = E.inOutCubic(prog(t, FLY[0], FLY[1]));
      set(page, { display: t >= FLY[0] && t < b(50.2) ? "block" : "none" });
      if (t >= FLY[0]) {
        const [sx, sy, z] = toScreen(sheetPt([36, 36]), t);
        const w0 = 30 * K * z;
        const pr = phone.pageRect(pxy[0], pxy[1], ps_);
        const bx = pr.x + pr.w / 2, by = pr.y + pr.h / 2;
        const cxp = (sx + bx) / 2, cyp = Math.min(sy, by) - (V ? 260 : 220);
        const qx = (1 - f) * (1 - f) * sx + 2 * (1 - f) * f * cxp + f * f * bx;
        const qy = (1 - f) * (1 - f) * sy + 2 * (1 - f) * f * cyp + f * f * by;
        const w = lerp(w0, pr.w, f), sc = w / 240, h = 340 * sc;
        set(page, { transform: "translate(" + (qx - w / 2) + "px," + (qy - h / 2) + "px) rotate(" + Math.sin(Math.PI * f) * -7 + "deg) scale(" + sc + ")", opacity: clamp(prog(t, FLY[0], FLY[0] + 0.08)) });
      }
    }

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

    if (t < b(3.4)) {
      set(titles, { display: "block" });
      set(hook1, { display: "block" }); set(hook2, { display: "block" });
      const l1 = E.outExpo(prog(t, 0, 0.5)), l2 = E.outExpo(prog(t, b(1), b(1) + 0.5));
      const o1 = E.inCubic(prog(t, b(2.55), b(2.55) + 0.26)), o2 = E.inCubic(prog(t, b(2.62), b(2.62) + 0.26));
      set(hook1.firstChild.firstChild, { transform: "translateY(" + ((1 - l1) * 110 - o1 * 110) + "%)" });
      set(hook2.firstChild.firstChild, { transform: "translateY(" + ((1 - l2) * 110 - o2 * 110) + "%)" });
      const pop = spring(t - b(1.5), 0.34, 0.62);
      const mv = E.inOutCubic(prog(t, b(2.62), b(3.15)));
      const pr = prog(t, b(2.5) - 0.04, b(2.5)) * (1 - prog(t, b(2.5) + 0.09, b(2.5) + 0.2));
      bigKey.update({ x: W / 2, y: lerp(LY.hookKey, LY.keyY, mv), show: t >= b(1.5) ? pop : 0, press: pr, scale: lerp(1, 96 / 168, mv) });
    } else {
      const pr = prog(t, b(3.5) - 0.04, b(3.5)) * (1 - prog(t, b(3.5) + 0.08, b(3.5) + 0.18));
      const hide = E.inCubic(prog(t, b(4.25), b(4.5)));
      set(titles, { display: hide < 1 ? "block" : "none" });
      set(hook1, { display: "none" }); set(hook2, { display: "none" });
      bigKey.update({ x: W / 2, y: LY.keyY, show: 1 - hide, press: pr, scale: 96 / 168 });
    }

    montage.update(t);

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
        const curIn = E.outCubic(prog(t, b(64.85), b(65.4)));
        const press = prog(t, b(65.42), b(65.5)) * (1 - prog(t, b(65.6), b(65.85)));
        const popAmt = t >= b(65.5) ? spring(t - b(65.5), 0.36, 0.5) : 0;
        mark.update({ pop: popAmt, cursor: curIn, press, shadow: clamp(prog(e, 0.8, 1)) });
        const n = E.outExpo(prog(t, b(65.75), b(65.75) + 0.6));
        set(name, { top: LY.nameY + "px", display: t >= b(65.7) ? "block" : "none", letterSpacing: lerp(0.05, -0.02, E.outCubic(prog(t, b(65.75), b(65.75) + 0.8))) + "em" });
        set(name.firstChild.firstChild, { transform: "translateY(" + (1 - n) * 105 + "%)" });
        const by = E.outCubic(prog(t, b(66.25), b(66.25) + 0.5));
        set(byline, { top: LY.byY + "px", opacity: by, transform: "translateY(" + (1 - by) * 14 + "px)" });
        const ft = E.outCubic(prog(t, b(66.75), b(66.75) + 0.5));
        set(foot, { top: LY.footY + "px", opacity: ft });
        const push = 1 + 0.03 * E.inOutSine(prog(t, b(66), b(74)));
        const nudge = t >= b(72) ? 1 + 0.022 * Math.sin(Math.PI * prog(t, b(72), b(72) + 0.3)) : 1;
        set(fin, { transform: "scale(" + push * nudge + ")", transformOrigin: W / 2 + "px " + (LY.icon.y + LY.icon.s * 0.45) + "px" });
      }
    }
  }

  window.DURATION = DURATION;
  window.renderAt = renderAt;
  window.AUDIO_EVENTS = AUDIO.sort((a, c) => a.t - c.t);
  document.documentElement.lang = LANG === "ko" ? "ko" : "en";
  const faces = ["400 20px 'PV Segoe'", "600 20px 'PV Segoe'", "350 20px 'PV Segoe'", "700 20px 'PV Segoe Sans'", "400 20px 'PV Malgun'", "700 20px 'PV Malgun'",
    "400 20px 'PV Roboto'", "700 20px 'PV Roboto'", "400 20px 'PV SF Mono'", "700 20px 'PV SF Mono'"].map((f) => document.fonts.load(f, f.includes("Malgun") ? "가나다" : "Aa"));
  const icons = ["20px 'PV FluentR'", "20px 'PV FluentF'"].map((f, i) => document.fonts.load(f, i ? String.fromCodePoint(PV.GLYPHS["fill.desktop"][1]) : String.fromCodePoint(PV.GLYPHS.photo[1])));
  const pics = ["assets/lisbon.jpg", "assets/beach.jpg", "assets/latte.jpg", "assets/shiba.jpg", "assets/shiba-cutout.png"];
  window.ready = Promise.all([...faces, ...icons, document.fonts.ready])
    .then(() => Promise.all(pics.map((src) => { const im = new Image(); im.src = src; return im.decode().catch(() => 0); })))
    .then(() => { (PV.afterFonts || []).forEach((fn) => fn()); renderAt(+(params.get("t") || 0)); return true; });
})();


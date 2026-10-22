// Pickverter promo — 35 s, 1920×1080. renderAt(t) draws the frame for time t (seconds); nothing depends on wall time.
(function () {
  const { el, sym, set, attr, lerp, clamp, prog, E, spring, kf, win, polar } = PV;
  const params = new URLSearchParams(location.search);
  const LANG = window.LANG || params.get("lang") || "en";
  const DURATION = 35;

  // ------------------------------------------------------------------ copy
  const I18N = window.I18N || { languages: ["en"], strings: {} };
  const li = Math.max(0, I18N.languages.indexOf(LANG));
  const COPY = {
    en: {
      "promo.favorites": "Favorites", "promo.recents": "Recents", "promo.applications": "Applications",
      intro1: "Every file.", intro2: "One key away.",
      c1h: "Select a file. Tap ⌥.", c1s: "The wheel opens right at your cursor.",
      c2h: "Convert in a flick.", c2s: "Images, video, audio, PDFs and documents.",
      c3h: "Hover to choose how.", c3s: "Every tool fans out its options.",
      c4h: "Save it. Or just copy it.", c4s: "Send results to a file, the clipboard, or both.",
      c5h: "AirDrop, Terminal, and more.", c5s: "Copy paths, share, float a preview — without leaving Finder.",
      words: ["Trim video.", "Crop.", "Blur faces.", "Organize PDFs.", "Make GIFs.", "Extract text.", "Join clips.", "Excel → CSV."],
      langH: "Speaks your language.",
      values: ["Runs on your Mac.", "Free & open source.", "Updates itself."],
      foot: "Free for macOS  ·  github.com/ivLis-Studio/pickverter",
      done: "On the clipboard",
    },
    ko: {
      "promo.favorites": "즐겨찾기", "promo.recents": "최근 항목", "promo.applications": "응용 프로그램",
      intro1: "모든 파일을,", intro2: "키 하나로.",
      c1h: "파일을 고르고 ⌥를 톡.", c1s: "커서 바로 옆에 휠이 열려요.",
      c2h: "휙, 한 번에 변환.", c2s: "이미지·동영상·오디오·PDF·문서까지.",
      c3h: "올려 두면 옵션이 펼쳐져요.", c3s: "회전 방향, 크기, 품질까지 그 자리에서.",
      c4h: "파일로 저장하거나, 그냥 복사하거나.", c4s: "결과를 파일·클립보드·둘 다 중에서 고르세요.",
      c5h: "AirDrop, 터미널, 그 밖에도.", c5s: "경로 복사, 공유, 미리보기 띄우기까지 Finder 안에서.",
      words: ["동영상 자르기.", "원하는 만큼 크롭.", "얼굴 가리기.", "PDF 페이지 정리.", "GIF 만들기.", "글자 추출.", "클립 합치기.", "Excel → CSV."],
      langH: "10개 언어로 말해요.",
      values: ["모든 처리는 내 Mac에서.", "무료 오픈 소스.", "업데이트는 알아서."],
      foot: "macOS용 무료 앱  ·  github.com/ivLis-Studio/pickverter",
      done: "클립보드에 복사됨",
    },
  };
  const C = COPY[LANG] || COPY.en;
  function fmt(s, args) {
    return s.replace(/%(\d)\$@/g, (_, n) => args[+n - 1]).replace(/%@/g, () => args.shift());
  }
  const raw = (key) => C[key] ?? (I18N.strings[key] ? I18N.strings[key][li] : null) ?? COPY.en[key] ?? key;
  PV.t = (key, ...args) => fmt(raw(key), args.slice());
  PV.tp = (key, n) => {
    const forms = raw(key).split("|");
    let f = forms[0];
    if (forms.length === 2) f = n === 1 ? forms[0] : forms[1];
    if (forms.length === 3) { const m10 = n % 10, m100 = n % 100; f = m10 === 1 && m100 !== 11 ? forms[0] : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? forms[1] : forms[2]; }
    return f.replace(/%1\$@|%@/g, String(n));
  };
  const T = PV.t;

  // ------------------------------------------------------------------ stage
  const UI = PV.UI, K = UI.K, G = UI.G;
  const stage = document.getElementById("stage");
  const cam = el("div", { class: "abs layer" }, stage);
  const over = el("div", { class: "abs layer" }, stage);
  const titles = el("div", { class: "abs layer" }, stage);
  const fin = el("div", { class: "abs layer" }, stage);
  const AUDIO = [];
  const sfx = (name, t, extra = {}) => AUDIO.push(Object.assign({ name, t: Math.round(t * 1000) / 1000 }, extra));

  // ------------------------------------------------------------------ data
  const FILES = [
    { name: "Lisbon.heic", photo: "assets/lisbon.jpg", w: 90, h: 60, slot: 0 },
    { name: "Shiba.jpg", photo: "assets/shiba.jpg", w: 58, h: 87, slot: 1 },
    { name: "Interview.mov", icon: "assets/icons/mov.png", slot: 2 },
    { name: "Q3 Report.pdf", icon: "assets/icons/pdf.png", slot: 3 },
    { name: "Podcast.mp3", icon: "assets/icons/mp3.png", slot: 4 },
    { name: "Pitch.key", icon: "assets/icons/key.png", slot: 5 },
    { name: "Budget.xlsx", icon: "assets/icons/xlsx.png", slot: 6 },
    { name: "Lisbon.png", photo: "assets/lisbon.jpg", w: 90, h: 60, slot: 7 },
  ];
  const fmtRing = (list) => list.map((title) => ({ title, format: true }));
  const v = (title, symbol, d) => ({ title, symbol, default: !!d });
  const RINGS = {
    convertHeic: fmtRing(["JPG", "PNG", "WebP", "AVIF", "TIFF", "GIF", "BMP", "PDF", "ICO"]),
    convertJpg: fmtRing(["PNG", "HEIC", "WebP", "AVIF", "TIFF", "GIF", "BMP", "PDF", "ICO"]),
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
    actions: [
      { title: "AirDrop", symbol: "dot.radiowaves.left.and.right", detail: T("action.airdrop.d") },
      { title: T("action.terminal"), symbol: "terminal", detail: T("action.terminal.d", T("action.terminal")) },
      { title: T("action.copypath"), symbol: "link", variants: [v(T("path.full"), "link", 1), v(T("path.tilde"), "link"), v(T("path.name"), "link"), v(T("path.quoted"), "link")] },
      { title: T("action.copyfile"), symbol: "doc.on.doc" },
      { title: T("action.move"), symbol: "folder", variants: [v(T("folder.choose"), "folder", 1), v(T("folder.desktop"), "menubar.dock.rectangle"), v(T("folder.downloads"), "arrow.down.circle")] },
      { title: T("action.duplicate"), symbol: "plus.square.on.square", variants: [v(T("folder.choose"), "folder", 1), v(T("folder.desktop"), "menubar.dock.rectangle"), v(T("folder.downloads"), "arrow.down.circle")] },
      { title: T("action.float"), symbol: "pin" },
      { title: T("action.share"), symbol: "square.and.arrow.up" },
      { title: "VS Code", symbol: "chevron.left.forwardslash.chevron.right" },
      { title: T("action.hash"), symbol: "number", variants: [v("SHA-256", "number", 1), v("SHA-1", "number"), v("MD5", "number")] },
    ],
    choice: [
      { title: T("dest.file"), symbol: "doc" },
      { title: T("dest.clipboard"), symbol: "doc.on.clipboard" },
      { title: T("dest.both"), symbol: "doc.on.doc" },
    ],
  };

  // ------------------------------------------------------------------ Finder + cursor path
  const WX = Math.round((1920 - UI.WIN.w * K) / 2), WY = 282;
  const finder = UI.Finder(cam, FILES, WX, WY);
  const LC = finder.at("Lisbon.heic"), SC = finder.at("Shiba.jpg");
  const slot = (c, n, i, r = 106) => polar(c[0], c[1], r * K, -90 + (360 / n) * i);
  const SUB_R = (G.subInner + G.subOuter) / 2;

  const moves = [];
  let last = { t: 0, p: [1700, 1180] };
  function to(t0, t1, p, bend = 0.12, ease = E.inOutCubic) { moves.push({ kind: "line", t0, t1, a: last.p, b: p, bend, ease }); last = { t: t1, p }; }
  function arc(t0, t1, c, r, a0, a1, ease = E.inOutCubic) {
    const b = polar(c[0], c[1], r * K, a1);
    moves.push({ kind: "arc", t0, t1, c, r: r * K, a0, a1, ease }); last = { t: t1, p: b };
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

  // ------------------------------------------------------------------ timeline
  // Intro 0–2 · Convert 2–7.5 · Rotate 7.5–11 · Cut out 11–15 · Actions 15–19.8 · Montage 20–24 · Languages 24–26 · Values 26–28 · Finale 28–35
  to(2.55, 3.25, LC, 0.16);
  click(3.32);
  // ⌥ → wheel (convert)
  to(4.4, 4.64, slot(LC, 9, 0), 0.0, E.outCubic);
  arc(4.74, 5.02, LC, 106, -90, -50);
  click(5.42);
  // Shiba: ⌥ ⌘ → tools → rotate → right 90°
  to(7.45, 7.95, SC, 0.14);
  click(8.02);
  to(9.05, 9.4, slot(SC, 9, 6), 0.0, E.outCubic);
  to(9.52, 9.74, polar(SC[0], SC[1], SUB_R * K, 150), 0.0);
  arc(9.86, 10.22, SC, SUB_R, 150, 96);
  click(10.4);
  to(10.62, 11.0, SC, 0.1);
  // ⌥ ⌘ → remove background → ask where → clipboard
  to(11.86, 12.16, slot(SC, 9, 3), 0.0, E.outCubic);
  click(12.45);
  arc(12.66, 12.9, SC, 106, 30, -82);
  arc(13.08, 13.32, SC, 106, -82, 26);
  click(13.5);
  to(14.9, 15.3, SC, 0.1);
  // ⌥ ⌃ → actions → Terminal
  to(16.1, 16.4, slot(SC, 10, 0), 0.0, E.outCubic);
  arc(16.64, 16.9, SC, 106, -90, -54);
  click(17.15);
  to(17.5, 18.3, [1310, 860], 0.2);

  const wheels = [
    UI.Wheel(cam, RINGS, { id: 1, at: LC, open: 4.0, close: 5.44, pick: true, modes: [{ t: 4.0, ring: "convertHeic" }], cursor: cursorAt,
      file: { name: "Lisbon.heic", thumb: "assets/lisbon.jpg" }, preview: (it) => "Lisbon." + it.title.toLowerCase() }),
    UI.Wheel(cam, RINGS, { id: 2, at: SC, open: 8.44, close: 10.42, pick: true, modes: [{ t: 8.44, ring: "convertJpg" }, { t: 8.88, ring: "tools" }], cursor: cursorAt,
      file: { name: "Shiba.jpg", thumb: "assets/shiba.jpg" }, preview: (it) => "Shiba." + it.title.toLowerCase() }),
    UI.Wheel(cam, RINGS, { id: 3, at: SC, open: 11.36, close: 13.52, pick: true, modes: [{ t: 11.36, ring: "convertJpg" }, { t: 11.72, ring: "tools" }], choice: { t: 12.47, ring: "choice" },
      choiceOption: T("tool.removebg"), cursor: cursorAt, file: { name: "Shiba.jpg", thumb: "assets/shiba.jpg" }, preview: (it) => "Shiba." + it.title.toLowerCase() }),
    UI.Wheel(cam, RINGS, { id: 4, at: SC, open: 15.6, close: 17.17, pick: true, modes: [{ t: 15.6, ring: "convertJpg" }, { t: 15.98, ring: "actions" }], cursor: cursorAt,
      file: { name: "Shiba.jpg", thumb: "assets/shiba.jpg" }, preview: (it) => "Shiba." + it.title.toLowerCase() }),
  ];
  for (const w of wheels) w.ticks.forEach((t) => sfx("tick", t));
  [[4.0, 5.44], [8.44, 10.42], [11.36, 13.52], [15.6, 17.17]].forEach(([o, c]) => { sfx("open", o); sfx("close", c); });
  [8.88, 11.72, 15.98].forEach((t) => sfx("swap", t));
  sfx("choice", 12.47);

  const cursor = UI.Cursor(cam);

  // Keycaps under the window: groups of presses shown side by side.
  const KEY = { opt: ["⌥", "option"], cmd: ["⌘", "command"], ctrl: ["⌃", "control"] };
  const keyGroups = [
    { keys: [["opt", 3.5, 3.94]], hide: 4.6 },
    { keys: [["opt", 8.12, 8.38], ["cmd", 8.6, 8.82]], hide: 9.35 },
    { keys: [["opt", 11.1, 11.3], ["cmd", 11.48, 11.66]], hide: 12.15 },
    { keys: [["opt", 15.35, 15.55], ["ctrl", 15.75, 15.92]], hide: 16.45 },
  ];
  const keycaps = [];
  for (const g of keyGroups) {
    g.keys.forEach(([k, show, press], i) => {
      const cap = UI.Keycap(over, ...KEY[k]);
      keycaps.push({ cap, g, i, show, press });
      sfx("key", press);
    });
  }

  // Result cards (top-right corner, like notifications).
  const cardX = 1920 - 44 - 352, cardY = 44;
  const card1 = UI.Card(over, { x: cardX, y: cardY, in: 5.62, done: 6.18, out: 7.45, symbol: "photo", thumb: "assets/lisbon.jpg",
    runTitle: T("job.running", T("job.convert_label", "PNG")), runSub: "Lisbon.heic",
    doneTitle: T("job.done", T("job.convert_label", "PNG")), doneSub: "Lisbon.png · 4.2 MB",
    buttons: [{ title: T("card.reveal"), symbol: "folder" }, { title: T("card.open"), symbol: "arrow.up.forward.app" }, { title: T("card.copy"), symbol: "doc.on.clipboard" }, { title: T("card.undo"), symbol: "arrow.uturn.backward" }] });
  const card2 = UI.Card(over, { x: cardX, y: cardY, in: 13.66, done: 14.08, out: 15.15, symbol: "scissors", thumb: "assets/shiba-cutout.png",
    runTitle: T("job.running", T("tool.removebg")), runSub: "Shiba.jpg",
    doneTitle: T("job.done", T("tool.removebg")), doneSub: C.done,
    buttons: [{ title: T("card.save"), symbol: "square.and.arrow.down" }, { title: T("card.undo"), symbol: "arrow.uturn.backward" }] });
  sfx("card", 5.62); sfx("done", 6.18); sfx("pop", 6.24); sfx("card", 13.66); sfx("done", 14.08);
  sfx("swish", 10.48); sfx("lift", 13.56);

  // Cut-out lifting off the photo.
  const cut = el("div", { class: "abs" }, cam);
  const cutImg = el("img", { src: "assets/shiba-cutout.png" }, cut);
  set(cut, { zIndex: 45, filter: "drop-shadow(0 26px 30px rgba(0,0,0,0.22))" });

  // Terminal.
  const term = UI.Terminal(cam, 1010, 470, {
    title: T("folder.desktop") + " — -zsh — 80×24", login: "Last login: Wed Oct 22 19:24:03 on ttys002",
    prompt: "ivlis@MacBook-Pro Desktop %", command: "ls",
    output: "Budget.xlsx     Lisbon.heic     Pitch.key       Q3 Report.pdf\nInterview.mov   Lisbon.png      Podcast.mp3     Shiba.jpg",
  });
  const TERM = { in: 17.3, typeAt: 17.78, outAt: 18.12, out: 19.3 };
  sfx("term", 17.3); sfx("type", 17.78); sfx("type", 17.85); sfx("enter", 18.06);

  // Captions.
  // Captions never overlap: each one is gone before the next rises.
  const caps = [["c1", 2.85, 4.3], ["c2", 4.6, 7.3], ["c3", 7.6, 10.9], ["c4", 11.2, 15.05], ["c5", 15.35, 19.3]].map(([k, a, b]) => {
    const node = el("div", { class: "cap" }, over, '<div class="cap-h">' + C[k + "h"] + '</div><div class="cap-s">' + C[k + "s"] + "</div>");
    set(node, { top: "92px" });
    return { node, a, b };
  });

  // ------------------------------------------------------------------ titles: intro
  const intro1 = el("div", { class: "big" }, titles, '<span class="mask"><span>' + C.intro1 + "</span></span>");
  const intro2 = el("div", { class: "big" }, titles, '<span class="mask"><span>' + C.intro2 + "</span></span>");
  set(intro1, { top: "300px" }); set(intro2, { top: "440px" });
  const bigKey = UI.Keycap(titles, ...KEY.opt, true);
  sfx("key", 1.6, { big: true }); sfx("whoosh", 2.1);

  // Montage.
  const MSYM = ["timeline.selection", "crop", "person.crop.square", "rectangle.stack", "photo.on.rectangle", "text.viewfinder", "film.stack", "tablecells"];
  const mTile = el("div", { class: "tile" }, titles);
  set(mTile, { left: 960 - 100 + "px", top: 270 + "px", width: "200px", height: "200px", borderRadius: "44px" });
  const mSyms = MSYM.map((s) => sym(s, 104, "var(--accent)", mTile, { position: "absolute" }));
  // Words swap like a slot machine: each sits in its own clipping mask, so nothing ever overlaps.
  const mWords = C.words.map((w) => el("div", { class: "big" }, titles, '<span class="mask"><span>' + w + "</span></span>"));
  C.words.forEach((_, i) => sfx("word", 20 + i * 0.5, { i }));

  // Languages.
  const LANGS = ["en", "ko", "ja", "zh-Hans", "zh-Hant", "es", "fr", "de", "ru", "pt-BR"];
  const LNAMES = ["English", "한국어", "日本語", "简体中文", "繁體中文", "Español", "Français", "Deutsch", "Русский", "Português"];
  const langH = el("div", { class: "cap" }, titles, '<div class="cap-h">' + C.langH + "</div>");
  set(langH, { top: "300px" });
  const convertWords = LANGS.map((l) => {
    const i = I18N.languages.indexOf(l);
    return i >= 0 && I18N.strings["mode.convert"] ? I18N.strings["mode.convert"][i] : "Convert";
  });
  const langWords = convertWords.map((w) => el("div", { class: "big" }, titles, '<span class="mask"><span>' + w + "</span></span>"));
  const langNames = LNAMES.map((n) => el("div", { class: "lang-name" }, titles, n));
  LANGS.forEach((_, i) => sfx("blip", 24.15 + i * 0.18, { i }));

  // Values.
  const VSYM = ["lock.shield", "chevron.left.forwardslash.chevron.right", "arrow.down.circle"];
  const values = C.values.map((txt, i) => {
    const node = el("div", { class: "value" }, titles);
    const tile = el("div", { class: "tile", style: { position: "relative", margin: "0 auto", width: "150px", height: "150px", borderRadius: "34px" } }, node);
    sym(VSYM[i], 74, "var(--accent)", tile);
    el("div", { class: "v-t" }, node, txt);
    set(node, { left: 960 + (i - 1) * 560 - 260 + "px", top: "360px" });
    sfx("word", 26.05 + i * 0.42, { i: i + 2 });
    return node;
  });

  // ------------------------------------------------------------------ finale
  const markBox = el("div", { class: "abs" }, fin);
  const mark = PV.Icon(markBox, 1024, "fin");
  set(mark.svg, { position: "absolute", left: 0, top: 0 });
  const FMT = ["JPG", "PNG", "WebP", "AVIF", "TIFF", "GIF", "PDF", "ICO"];
  const fLabels = FMT.map((f) => el("div", { class: "flabel" }, markBox, f));
  const hubLabel = el("div", { class: "flabel" }, markBox, "");
  const name = el("div", { class: "name" }, fin, '<span class="mask"><span>Pickverter</span></span>');
  const byline = el("div", { class: "byline" }, fin, "by <b>ivLis STUDIO</b>");
  const foot = el("div", { class: "foot" }, fin, C.foot);
  // Roulette chase: slow → fast → slow, landing on the picked slot.
  const STEPS = 2 * 8 + 1;
  const chase = [];
  for (let k = 0; k <= STEPS; k++) {
    const u = k / STEPS;
    const g = u + (0.72 * Math.sin(2 * Math.PI * u)) / (2 * Math.PI);
    chase.push({ t: 28.3 + 1.26 * g, i: k % 8 });
  }
  chase.forEach((c, k) => sfx("tick", c.t, { pitch: k / STEPS, chase: true }));
  sfx("bloom", 28.0); sfx("riser", 28.4, { until: 29.84 }); sfx("whoosh_big", 29.84); sfx("impact", 30.36);
  sfx("click", 30.7, { icon: true }); sfx("shing", 31.08); sfx("chime", 31.45);
  const FIN = { open: 28.0, morph: 29.86 };
  // Sub-frame motion blur where things move fast (render.mjs averages these samples).
  window.MOTION_BLUR = [{ from: 29.8, to: 30.8, samples: 10 }, { from: 31.05, to: 31.5, samples: 6 }];

  // ------------------------------------------------------------------ render
  function renderAt(t) {
    // background layers
    const demo = t >= 1.9 && t < 20.1;
    set(cam, { display: demo ? "block" : "none" });
    set(over, { display: demo ? "block" : "none" });

    // Finder window
    const wIn = E.outCubic(prog(t, 2.14, 2.76)), wOut = E.inCubic(prog(t, 19.35, 19.85));
    finder.update({
      opacity: wIn * (1 - wOut), dy: (1 - wIn) * 70 + wOut * 50, scale: (0.97 + 0.03 * wIn) * (1 - 0.03 * wOut),
      selected: t < 7.98 ? (t >= 3.32 ? "Lisbon.heic" : null) : t >= 8.02 ? "Shiba.jpg" : null,
      appear: { "Lisbon.png": spring(t - 6.24, 0.42, 0.62) },
      rotate: { "Shiba.jpg": -90 + 90 * spring(t - 10.48, 0.5, 0.62) },
      hideImg: { "Shiba.jpg": 0 },
    });

    // cursor
    const [cx, cy] = cursorAt(t);
    let press = 0, ring = 0;
    for (const c of clicks) {
      press = Math.max(press, prog(t, c - 0.05, c) * (1 - prog(t, c + 0.02, c + 0.14)));
      if (t >= c && t < c + 0.45) ring = prog(t, c, c + 0.45);
    }
    const cOp = E.outCubic(prog(t, 2.55, 2.75)) * (1 - prog(t, 19.35, 19.6));
    cursor.update({ x: cx, y: cy, press, ring, opacity: cOp });

    for (const w of wheels) w.update(t);

    // keycaps
    for (const kc of keycaps) {
      const n = kc.g.keys.length;
      const visible = t >= kc.show - 0.01 && t <= kc.g.hide + 0.3;
      const second = n > 1 ? kc.g.keys[1][1] : Infinity;
      const spread = n > 1 ? spring(t - second, 0.3, 0.82) : 0;
      const x = 960 + (n > 1 ? (kc.i === 0 ? -60 : 60) * (kc.i === 0 ? spread : 1) : 0);
      const pop = visible ? spring(t - kc.show, 0.3, 0.7) * (1 - E.inCubic(prog(t, kc.g.hide, kc.g.hide + 0.22))) : 0;
      const pr = prog(t, kc.press - 0.04, kc.press) * (1 - prog(t, kc.press + 0.08, kc.press + 0.18));
      kc.cap.update({ x, y: 990, show: pop, press: pr });
    }

    card1.update(t);
    card2.update(t);

    // cut-out
    {
      const [sx, sy] = SC;
      const a = spring(t - 13.56, 0.55, 0.78);
      const out = E.inCubic(prog(t, 14.85, 15.2));
      const h0 = 87 * K, h1 = 560;
      const h = lerp(h0, h1, a) * (1 - 0.12 * out);
      const x = lerp(sx, 1330, a), y = lerp(sy, 600, a) - Math.sin(t * 2.2) * 6 * a;
      const visible = t >= 13.56 && t < 15.25;
      set(cut, { display: visible ? "block" : "none", transform: "translate(" + (x - (h * 1066) / 1600 / 2) + "px," + (y - h / 2) + "px)", opacity: clamp(a * 3) * (1 - out) });
      set(cutImg, { height: h + "px", width: (h * 1066) / 1600 + "px" });
    }

    term.update(t, TERM);

    for (const c of caps) {
      const a = E.outCubic(prog(t, c.a, c.a + 0.4)), o = E.inCubic(prog(t, c.b, c.b + 0.22));
      set(c.node, { opacity: a * (1 - o), transform: "translateY(" + ((1 - a) * 18 - o * 10) + "px)", display: t >= c.a && t <= c.b + 0.25 ? "block" : "none" });
    }

    // intro titles
    {
      const out = E.inCubic(prog(t, 1.9, 2.16));
      const l1 = E.outExpo(prog(t, 0.22, 0.95)), l2 = E.outExpo(prog(t, 0.78, 1.5));
      set(intro1.firstChild.firstChild, { transform: "translateY(" + (1 - l1) * 110 + "%)" });
      set(intro2.firstChild.firstChild, { transform: "translateY(" + (1 - l2) * 110 + "%)" });
      set(intro1, { opacity: 1 - out, transform: "translateY(" + -out * 60 + "px)", display: t < 2.4 ? "block" : "none" });
      set(intro2, { opacity: 1 - out, transform: "translateY(" + -out * 60 + "px)", display: t < 2.4 ? "block" : "none" });
      const kpop = spring(t - 1.2, 0.34, 0.66) * (1 - out);
      const kpr = prog(t, 1.56, 1.6) * (1 - prog(t, 1.7, 1.82));
      bigKey.update({ x: 960, y: 715, show: t < 2.4 ? kpop : 0, press: kpr });
    }

    // montage
    {
      const on = t >= 19.9 && t < 24.05;
      const tileIn = spring(t - 19.92, 0.4, 0.72), tileOut = E.inCubic(prog(t, 23.84, 24.02));
      set(mTile, { display: on ? "flex" : "none", opacity: clamp(tileIn * 2) * (1 - tileOut), transform: "scale(" + (0.6 + 0.4 * tileIn) * (1 - 0.2 * tileOut) + ")" });
      mSyms.forEach((s, i) => {
        const a = prog(t, 20 + i * 0.5, 20 + i * 0.5 + 0.18);
        const b = i < 7 ? prog(t, 20.5 + i * 0.5, 20.5 + i * 0.5 + 0.1) : 0;
        set(s, { opacity: E.outCubic(a) * (1 - b), transform: "scale(" + (0.6 + 0.4 * spring(t - (20 + i * 0.5), 0.3, 0.6)) + ")" });
      });
      mWords.forEach((w, i) => {
        const t0 = 20 + i * 0.5;
        // Incoming and outgoing words share one easing, so they stay exactly one line apart.
        const a = E.outExpo(prog(t, t0, t0 + 0.32));
        const t1 = i < 7 ? t0 + 0.5 : 23.84;
        const b = i < 7 ? E.outExpo(prog(t, t1, t1 + 0.32)) : E.inCubic(prog(t, t1, t1 + 0.16));
        set(w, { top: "540px", display: on && t >= t0 - 0.01 && t <= t1 + 0.33 ? "block" : "none" });
        set(w.firstChild.firstChild, { transform: "translateY(" + ((1 - a) * 110 - b * 110) + "%)" });
      });
    }

    // languages
    {
      const on = t >= 24.04 && t < 26.05;
      const h = E.outCubic(prog(t, 24.05, 24.45)), ho = E.inCubic(prog(t, 25.8, 25.98));
      set(langH, { display: on ? "block" : "none", opacity: h * (1 - ho), transform: "translateY(" + ((1 - h) * 20) + "px)" });
      langWords.forEach((w, i) => {
        const t0 = 24.15 + i * 0.18, t1 = i < 9 ? t0 + 0.18 : 25.85;
        const a = E.outExpo(prog(t, t0, t0 + 0.17)), b = i < 9 ? E.outExpo(prog(t, t1, t1 + 0.17)) : E.inCubic(prog(t, t1, t1 + 0.1));
        const vis = on && t >= t0 && t <= t1 + 0.18;
        set(w, { top: "460px", display: vis ? "block" : "none" });
        set(w.firstChild.firstChild, { transform: "translateY(" + ((1 - a) * 110 - b * 110) + "%)" });
        set(langNames[i], { top: "640px", display: vis ? "block" : "none", opacity: E.outCubic(prog(t, t0, t0 + 0.08)) * (1 - prog(t, t1, t1 + 0.05)) });
      });
    }

    // values
    values.forEach((node, i) => {
      const t0 = 26.05 + i * 0.42;
      const a = spring(t - t0, 0.42, 0.76), o = E.inCubic(prog(t, 27.7, 27.95));
      set(node, { display: t >= t0 - 0.01 && t < 28.0 ? "block" : "none", opacity: clamp(a * 2) * (1 - o), transform: "translateY(" + ((1 - a) * 40 + o * -20) + "px)" });
    });

    // finale
    {
      const on = t >= FIN.open - 0.01;
      set(fin, { display: on ? "block" : "none" });
      if (on) {
        const open = spring(t - FIN.open, 0.5, 0.78);
        // Breathe in, then spin once while shrinking into the icon ("슉"), then a small bounce on the click.
        const breathe = 1 + 0.035 * Math.sin(Math.PI * prog(t, 29.6, 29.9));
        const shrink = E.inOutExpo(prog(t, 29.84, 30.34));
        const bounce = 1 - 0.04 * Math.sin(Math.PI * prog(t, 30.68, 30.95));
        const lift = E.inOutCubic(prog(t, 30.84, 31.34));
        const size = lerp(1040, 420, shrink) * (0.86 + 0.14 * open) * breathe * bounce;
        const cyM = lerp(540, 400, lift);
        const rot = 360 * E.inOutCubic(prog(t, 29.84, 30.4));
        set(markBox, { transform: "translate(" + (960 - size / 2) + "px," + (cyM - size / 2) + "px)", opacity: clamp(open * 2.2) });
        set(mark.svg, { width: size + "px", height: size + "px", transform: "rotate(" + rot + "deg)" });
        let hl = -1;
        for (const c of chase) if (t >= c.t) hl = c.i;
        const m = E.inOutCubic(prog(t, 29.96, 30.32));
        const tile = E.outBack(prog(t, 30.02, 30.44));
        const pop = t >= 30.26 ? spring(t - 30.26, 0.36, 0.55) : 0;
        const curIn = E.outCubic(prog(t, 30.32, 30.64));
        const pressI = prog(t, 30.64, 30.7) * (1 - prog(t, 30.8, 30.92));
        mark.update({ m, tile, lit: t >= chase[chase.length - 1].t ? 1 : 0, hl: t < 29.9 ? hl : -1, hlMix: 1, pop, cursor: curIn, press: pressI,
          doc: E.outCubic(prog(t, 30.14, 30.42)), spin: 0 });
        // labels on the light wheel
        const labelsOn = (1 - E.inQuad(prog(t, 29.74, 29.9)));
        const g = PV.ICON.geometry(0);
        const u = size / 1024;
        FMT.forEach((f, i) => {
          const [lx, ly] = polar(size / 2, size / 2, ((g.inner + g.outer) / 2 + 4) * u, -90 + 45 * i);
          set(fLabels[i], { left: lx - 80 + "px", top: ly - 30 + "px", width: "160px", height: "60px", fontSize: 14 * 2.08 * u + "px",
            color: hl === i && t < 29.9 ? "#fff" : "rgba(29,29,31,0.85)", opacity: labelsOn, display: labelsOn > 0.01 ? "flex" : "none" });
        });
        const hubText = hl >= 0 ? FMT[hl] : "Lisbon.heic";
        if (hubLabel.textContent !== hubText) hubLabel.textContent = hubText;
        set(hubLabel, { left: size / 2 - 150 + "px", top: size / 2 - 40 + "px", width: "300px", height: "80px", fontSize: (hl >= 0 ? 40 : 22) * u * 1.0 + "px",
          opacity: labelsOn, display: labelsOn > 0.01 ? "flex" : "none", color: "#1D1D1F" });
        // name + byline
        const n = E.outExpo(prog(t, 31.08, 31.66));
        set(name, { top: "604px", display: t >= 31.06 ? "block" : "none", letterSpacing: lerp(0.04, -0.02, E.outCubic(prog(t, 31.08, 31.76))) + "em" });
        set(name.firstChild.firstChild, { transform: "translateY(" + (1 - n) * 105 + "%)" });
        const b = E.outCubic(prog(t, 31.42, 31.95));
        set(byline, { top: "790px", opacity: b, transform: "translateY(" + (1 - b) * 14 + "px)" });
        const f = E.outCubic(prog(t, 31.95, 32.5));
        set(foot, { top: "990px", opacity: f });
      }
    }
  }

  window.DURATION = DURATION;
  window.renderAt = renderAt;
  window.AUDIO_EVENTS = AUDIO.sort((a, b) => a.t - b.t);
  document.documentElement.lang = LANG === "ko" ? "ko" : "en";
  const faces = ["400 20px 'PV SF'", "500 20px 'PV SF'", "600 20px 'PV SF'", "700 20px 'PV SF'", "400 20px 'PV SF Mono'"].map((f) => document.fonts.load(f));
  window.ready = Promise.all([...faces, document.fonts.ready, ...[...document.images].map((i) => (i.complete ? 0 : new Promise((r) => (i.onload = i.onerror = r))))])
    .then(() => Promise.all(["assets/lisbon.jpg", "assets/shiba.jpg", "assets/shiba-cutout.png"].map((src) => new Promise((r) => { const im = new Image(); im.onload = im.onerror = r; im.src = src; }))))
    .then(() => { (PV.afterFonts || []).forEach((fn) => fn()); renderAt(+(params.get("t") || 0)); return true; });
})();


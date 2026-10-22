// Feature montage on the accent blue: eight tools pop onto a ring (one every two beats) around a live vignette.
// Vignettes are drawn in a 400×400 box (centre 200,200) and clipped to the white disc.
(function () {
  const { el, sym, set, attr, lerp, clamp, prog, E, spring, polar } = PV;
  const NS = "http://www.w3.org/2000/svg";
  const BLUE = "#0A84FF";
  const INK = "#1D1D1F";
  let uid = 0;

  function S(tag, attrs, parent) {
    const n = document.createElementNS(NS, tag);
    for (const k in attrs) n.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(n);
    return n;
  }
  function clipRect(defs, x, y, w, h, r) {
    const id = "mc" + ++uid;
    const cp = S("clipPath", { id }, defs);
    const rect = S("rect", { x, y, width: w, height: h, rx: r }, cp);
    return { url: "url(#" + id + ")", rect };
  }
  function photo(parent, defs, src, x, y, w, h, r) {
    const g = S("g", { "clip-path": clipRect(defs, x, y, w, h, r).url }, parent);
    return S("image", { href: src, x, y, width: w, height: h, preserveAspectRatio: "xMidYMid slice" }, g);
  }
  const pop = (sec, at) => Math.max(0.001, spring(sec - at, 0.3, 0.55));

  // 1. Trim: QuickTime-style yellow handles close in, then the playhead runs.
  function vTrim(g, defs) {
    const x0 = 52, x1 = 348, y0 = 160, h = 80;
    S("rect", { x: x0 - 7, y: y0 - 7, width: x1 - x0 + 14, height: h + 14, rx: 15, fill: INK }, g);
    const frames = S("g", { "clip-path": clipRect(defs, x0, y0, x1 - x0, h, 9).url }, g);
    const fw = (x1 - x0) / 5;
    ["beach", "lisbon", "latte", "beach", "lisbon"].forEach((s, i) =>
      S("image", { href: "assets/" + s + ".jpg", x: x0 + i * fw, y: y0, width: fw + 0.5, height: h, preserveAspectRatio: "xMidYMid slice" }, frames));
    for (let i = 1; i < 5; i++) S("rect", { x: x0 + i * fw - 1, y: y0, width: 2, height: h, fill: INK, opacity: 0.55 }, frames);
    const dimL = S("rect", { x: x0, y: y0, width: 0, height: h, fill: "#000", opacity: 0.58 }, frames);
    const dimR = S("rect", { x: x1, y: y0, width: 0, height: h, fill: "#000", opacity: 0.58 }, frames);
    const frame = S("rect", { y: y0 - 3, height: h + 6, rx: 9, fill: "none", stroke: "#FFD60A", "stroke-width": 7 }, g);
    const handles = [-1, 1].map((dir) => {
      const hg = S("g", {}, g);
      S("rect", { x: -11, y: y0 - 6, width: 22, height: h + 12, rx: 7, fill: "#FFD60A" }, hg);
      const m = y0 + h / 2;
      S("path", { d: dir < 0 ? "M3 " + (m - 9) + " L-3 " + m + " L3 " + (m + 9) : "M-3 " + (m - 9) + " L3 " + m + " L-3 " + (m + 9),
        fill: "none", stroke: INK, "stroke-width": 3.2, "stroke-linecap": "round", "stroke-linejoin": "round" }, hg);
      return hg;
    });
    const head = S("rect", { y: y0 - 16, width: 6, height: h + 32, rx: 3, fill: "#fff", stroke: INK, "stroke-width": 1.5 }, g);
    const label = S("text", { x: 200, y: y0 + h + 70, "text-anchor": "middle", class: "m-num" }, g);
    return (u) => {
      const a = E.outExpo(prog(u, 0.06, 0.42)), c = E.outExpo(prog(u, 0.12, 0.48));
      const L = lerp(x0, 118, a), R = lerp(x1, 290, c);
      attr(dimL, "width", L - x0); attr(dimR, "x", R); attr(dimR, "width", x1 - R);
      attr(frame, "x", L); attr(frame, "width", R - L);
      attr(handles[0], "transform", "translate(" + L + " 0)");
      attr(handles[1], "transform", "translate(" + R + " 0)");
      attr(head, "x", lerp(L + 14, R - 14, E.inOutSine(prog(u, 0.45, 1))) - 3);
      attr(head, "opacity", clamp(prog(u, 0.4, 0.5)));
      label.textContent = "0:" + String(Math.round(lerp(42, 17, (a + c) / 2))).padStart(2, "0");
    };
  }

  // 2. Shrink: the size rolls down, the photo squeezes, a green arrow lands.
  function vCompress(g, defs) {
    const card = S("g", {}, g);
    photo(card, defs, "assets/latte.jpg", 105, 88, 190, 127, 16);
    S("rect", { x: 105, y: 88, width: 190, height: 127, rx: 16, fill: "none", stroke: "rgba(0,0,0,0.08)", "stroke-width": 1.5 }, card);
    S("rect", { x: 110, y: 242, width: 180, height: 56, rx: 28, fill: "#F2F2F7" }, g);
    const txt = S("text", { x: 200, y: 280, "text-anchor": "middle", class: "m-num" }, g);
    const badge = S("g", {}, g);
    S("circle", { cx: 0, cy: 0, r: 26, fill: "#30D158" }, badge);
    S("path", { d: "M0 -12 L0 11 M-9 3 L0 12 L9 3", fill: "none", stroke: "#fff", "stroke-width": 4.5, "stroke-linecap": "round", "stroke-linejoin": "round" }, badge);
    return (u, sec) => {
      const a = E.outExpo(prog(u, 0.1, 0.6));
      txt.textContent = lerp(24.6, 3.1, a).toFixed(1) + " MB";
      attr(card, "transform", "translate(200 151) scale(" + (1 - 0.07 * Math.sin(Math.PI * prog(u, 0.08, 0.36))) + ") translate(-200 -151)");
      attr(badge, "transform", "translate(292 94) scale(" + pop(sec, 0.46) + ")");
    };
  }

  // 3. GIF: frames flip in a loop, the badge pops.
  function vGif(g, defs) {
    S("rect", { x: 112, y: 104, width: 176, height: 118, rx: 16, fill: "#D1D1D6", transform: "rotate(-9 200 163)" }, g);
    S("rect", { x: 112, y: 104, width: 176, height: 118, rx: 16, fill: "#E5E5EA", transform: "rotate(6 200 163)" }, g);
    const front = S("g", {}, g);
    const fg = S("g", { "clip-path": clipRect(defs, 104, 96, 192, 128, 16).url }, front);
    const ims = ["beach", "lisbon", "latte"].map((s) => S("image", { href: "assets/" + s + ".jpg", x: 104, y: 96, width: 192, height: 128, preserveAspectRatio: "xMidYMid slice" }, fg));
    const dots = [0, 1, 2].map((i) => S("circle", { cx: 180 + i * 20, cy: 270, r: 5.5, fill: "#C7C7CC" }, g));
    const badge = S("g", {}, g);
    S("rect", { x: -44, y: -22, width: 88, height: 44, rx: 22, fill: INK }, badge);
    S("text", { x: 0, y: 9, "text-anchor": "middle", class: "m-badge" }, badge).textContent = "GIF";
    return (u, sec) => {
      const f = Math.max(0, (u - 0.1) * 10);
      const k = Math.floor(f) % 3;
      ims.forEach((im, i) => attr(im, "opacity", i === k ? 1 : 0));
      dots.forEach((d, i) => attr(d, "fill", i === k ? BLUE : "#C7C7CC"));
      attr(front, "transform", "translate(200 160) scale(" + (1 + 0.03 * Math.max(0, 1 - (f % 1) * 3) * (f > 0 ? 1 : 0)) + ") translate(-200 -160)");
      attr(badge, "transform", "translate(262 222) scale(" + pop(sec, 0.12) + ")");
    };
  }

  // 4. Faces: a detection box finds the face, mosaic tiles cover it.
  function vFaces(g, defs) {
    const pic = S("g", { "clip-path": clipRect(defs, 112, 86, 176, 228, 20).url }, g);
    S("rect", { x: 112, y: 86, width: 176, height: 228, fill: "#CFE3F7" }, pic);
    S("rect", { x: 112, y: 270, width: 176, height: 50, fill: "#BCD9AE" }, pic);
    S("path", { d: "M128 330 C128 262 160 248 200 248 C240 248 272 262 272 330 Z", fill: "#FF9F0A" }, pic);
    S("rect", { x: 188, y: 216, width: 24, height: 36, rx: 8, fill: "#DDAE84" }, pic);
    S("circle", { cx: 200, cy: 182, r: 46, fill: "#EBC199" }, pic);
    S("path", { d: "M152 184 C148 140 176 124 202 124 C230 124 254 142 248 180 C236 160 214 152 196 154 C178 156 162 166 152 184 Z", fill: "#4A3426" }, pic);
    const box = S("rect", { x: 144, y: 126, width: 112, height: 112, rx: 18, fill: "none", stroke: BLUE, "stroke-width": 4.5, "stroke-dasharray": "14 9" }, g);
    const tones = ["#E4B993", "#D9AA82", "#EDC7A2", "#C99B74", "#5A4030", "#E9BF98", "#4A3426", "#D2A47D",
      "#F0CDA9", "#C4936B", "#DDB089", "#E6BD95", "#CFA27C", "#BE9168", "#E0B58E", "#D8AB84"];
    const cells = [];
    for (let j = 0; j < 4; j++) for (let i = 0; i < 4; i++) {
      const cx = 150 + i * 25 + 12.5, cy = 132 + j * 25 + 12.5;
      const r = S("rect", { x: -12.75, y: -12.75, width: 25.5, height: 25.5, fill: tones[(j * 5 + i * 3) % 16] }, g);
      cells.push({ r, cx, cy, d: Math.hypot(i - 1.5, j - 1.5) });
    }
    return (u) => {
      const bx = E.outCubic(prog(u, 0.02, 0.2)) * (1 - E.inCubic(prog(u, 0.6, 0.76)));
      attr(box, "opacity", bx);
      attr(box, "transform", "translate(200 182) scale(" + lerp(1.3, 1, E.outCubic(prog(u, 0.02, 0.22))) + ") translate(-200 -182)");
      for (const c of cells) {
        const k = E.outBack(prog(u, 0.22 + c.d * 0.055, 0.36 + c.d * 0.055));
        attr(c.r, "transform", "translate(" + c.cx + " " + c.cy + ") scale(" + Math.max(0.001, k) + ")");
        attr(c.r, "opacity", clamp(k * 1.5));
      }
    };
  }

  // 5. Merge PDFs: two documents slide into one.
  function vMerge(g, defs) {
    function page(parent) {
      const pg = S("g", {}, parent);
      S("rect", { x: -60, y: -78, width: 120, height: 156, rx: 12, fill: "#fff", stroke: "#D1D1D6", "stroke-width": 2 }, pg);
      S("path", { d: "M-60 -46 V-66 A12 12 0 0 1 -48 -78 H48 A12 12 0 0 1 60 -66 V-46 Z", fill: "#FF3B30" }, pg);
      S("text", { x: 0, y: -55, "text-anchor": "middle", class: "m-tag" }, pg).textContent = "PDF";
      for (let i = 0; i < 5; i++) S("rect", { x: -42, y: -28 + i * 20, width: i === 4 ? 46 : 84, height: 8, rx: 4, fill: "#E5E5EA" }, pg);
      return pg;
    }
    const back = page(g), front = page(g);
    const plus = S("path", { d: "M-14 0 H14 M0 -14 V14", stroke: INK, "stroke-width": 6, "stroke-linecap": "round" }, g);
    const check = S("g", {}, g);
    S("circle", { cx: 0, cy: 0, r: 25, fill: BLUE }, check);
    S("path", { d: "M-10 1 L-3 8 L11 -7", fill: "none", stroke: "#fff", "stroke-width": 4.5, "stroke-linecap": "round", "stroke-linejoin": "round" }, check);
    return (u, sec) => {
      const a = E.inOutCubic(prog(u, 0.08, 0.48));
      attr(front, "transform", "translate(" + lerp(122, 192, a) + " " + lerp(200, 194, a) + ") rotate(" + lerp(-7, 0, a) + ")");
      attr(back, "transform", "translate(" + lerp(278, 212, a) + " " + lerp(204, 212, a) + ") rotate(" + lerp(7, 5, a) + ")");
      attr(plus, "transform", "translate(200 " + (300 - 8 * a) + ") scale(" + Math.max(0.001, 1 - a) + ")");
      attr(check, "transform", "translate(262 124) scale(" + pop(sec, 0.48) + ")");
    };
  }

  // 6. Grab text: a scan line passes, the lines get selected, "Copied".
  function vOcr(g, defs, L) {
    S("rect", { x: 100, y: 82, width: 200, height: 224, rx: 16, fill: "#FBF7EE", stroke: "#E8E1D3", "stroke-width": 2 }, g);
    const lines = [["Café Lisboa", 23, 700, "m-rcpt-h"], ["Latte      4.50", 17, 500, "m-rcpt"], ["Croissant  3.20", 17, 500, "m-rcpt"], ["Total      7.70", 17, 700, "m-rcpt"]];
    const ys = [134, 184, 218, 264];
    const hls = [], txts = [];
    lines.forEach(([s, size, w, cls], i) => {
      hls.push(S("rect", { x: 114, y: ys[i] - size - 3, width: 172, height: size + 13, rx: 6, fill: BLUE, opacity: 0 }, g));
      const tx = S("text", { x: 124, y: ys[i], class: cls, "font-size": size, "font-weight": w }, g);
      tx.textContent = s;
      txts.push(tx);
    });
    const scan = S("g", {}, g);
    S("rect", { x: 94, y: -30, width: 212, height: 30, fill: BLUE, opacity: 0.12 }, scan);
    S("rect", { x: 94, y: -2, width: 212, height: 4, rx: 2, fill: BLUE }, scan);
    const chip = S("g", {}, g);
    S("rect", { x: -82, y: -23, width: 164, height: 46, rx: 23, fill: INK }, chip);
    S("text", { x: 0, y: 8, "text-anchor": "middle", class: "m-chip" }, chip).textContent = L.copied;
    return (u, sec) => {
      const y = lerp(92, 300, E.inOutSine(prog(u, 0.04, 0.5)));
      attr(scan, "transform", "translate(0 " + y + ")");
      attr(scan, "opacity", clamp(prog(u, 0.02, 0.06)) * (1 - clamp(prog(u, 0.5, 0.56))));
      hls.forEach((h, i) => attr(h, "opacity", y > ys[i] - 4 ? 0.2 : 0));
      txts.forEach((tx, i) => attr(tx, "fill", y > ys[i] - 4 ? "#0A5FD8" : "#3A3A3C"));
      const cp = spring(sec - 0.48, 0.3, 0.6);
      attr(chip, "transform", "translate(200 " + (330 + (1 - cp) * 16) + ") scale(" + Math.max(0.001, lerp(0.7, 1, cp)) + ")");
      attr(chip, "opacity", clamp(cp * 2));
    };
  }

  // 7. Extract audio: the clip steps back and its sound wave grows out.
  function vAudio(g, defs) {
    const vid = S("g", {}, g);
    photo(vid, defs, "assets/beach.jpg", 120, 84, 160, 104, 14);
    S("circle", { cx: 200, cy: 136, r: 22, fill: "rgba(0,0,0,0.45)" }, vid);
    S("path", { d: "M193 125 L211 136 L193 147 Z", fill: "#fff" }, vid);
    const N = 15;
    const bars = [];
    for (let i = 0; i < N; i++) bars.push(S("rect", { x: 200 + (i - (N - 1) / 2) * 14 - 4, width: 8, rx: 4, fill: BLUE }, g));
    const badge = S("g", {}, g);
    S("rect", { x: -40, y: -20, width: 80, height: 40, rx: 20, fill: INK }, badge);
    S("text", { x: 0, y: 8, "text-anchor": "middle", class: "m-badge small" }, badge).textContent = "MP3";
    return (u, sec) => {
      const v = E.outCubic(prog(u, 0.05, 0.3));
      attr(vid, "transform", "translate(200 " + (136 - 10 * v) + ") scale(" + lerp(1, 0.86, v) + ") translate(-200 -136)");
      bars.forEach((r, i) => {
        const d = Math.abs(i - (N - 1) / 2);
        const grow = clamp(E.outBack(prog(u, 0.12 + d * 0.025, 0.3 + d * 0.025)), 0, 1.2);
        const wave = 0.5 + 0.5 * Math.abs(Math.sin(sec * 9 + i * 0.9) * Math.cos(sec * 4.3 + i * 0.37));
        const h = Math.max(8, 78 * (1 - d / 9) * wave * grow);
        attr(r, "y", 252 - h / 2); attr(r, "height", h); attr(r, "opacity", clamp(grow * 3));
      });
      attr(badge, "transform", "translate(200 334) scale(" + pop(sec, 0.42) + ")");
    };
  }

  // 8. Resize: the photo shrinks inside its old outline, the pixel size rolls down.
  function vResize(g, defs) {
    S("rect", { x: 60, y: 107, width: 280, height: 186, rx: 10, fill: "none", stroke: "#C7C7CC", "stroke-width": 3, "stroke-dasharray": "10 8" }, g);
    const clip = clipRect(defs, 60, 107, 280, 186, 10);
    const img = S("image", { href: "assets/lisbon.jpg", preserveAspectRatio: "xMidYMid slice", "clip-path": clip.url }, g);
    const handles = [0, 1, 2, 3].map(() => S("rect", { width: 16, height: 16, rx: 4, fill: "#fff", stroke: BLUE, "stroke-width": 3.5 }, g));
    const label = S("text", { x: 200, y: 344, "text-anchor": "middle", class: "m-num" }, g);
    return (u) => {
      const a = E.outExpo(prog(u, 0.1, 0.55));
      const w = lerp(280, 156, a), h = w / 1.5, x = 200 - w / 2, y = 200 - h / 2;
      for (const n of [clip.rect, img]) { attr(n, "x", x); attr(n, "y", y); attr(n, "width", w); attr(n, "height", h); }
      [[x, y], [x + w, y], [x, y + h], [x + w, y + h]].forEach(([hx, hy], i) => { attr(handles[i], "x", hx - 8); attr(handles[i], "y", hy - 8); });
      const w1 = Math.round(lerp(4032, 1080, a) / 2) * 2;
      label.textContent = w1 + " × " + Math.round(w1 * 0.75);
    };
  }

  // 9. Lock a PDF: the padlock snaps shut while the password fills in.
  function vLock(g, defs) {
    S("rect", { x: 120, y: 80, width: 160, height: 212, rx: 14, fill: "#fff", stroke: "#D1D1D6", "stroke-width": 2 }, g);
    S("path", { d: "M120 112 V94 A14 14 0 0 1 134 80 H266 A14 14 0 0 1 280 94 V112 Z", fill: "#FF3B30" }, g);
    S("text", { x: 200, y: 103, "text-anchor": "middle", class: "m-tag" }, g).textContent = "PDF";
    for (let i = 0; i < 4; i++) S("rect", { x: 140, y: 130 + i * 21, width: i === 3 ? 66 : 120, height: 9, rx: 4.5, fill: "#E5E5EA" }, g);
    const lock = S("g", {}, g);
    const shackle = S("path", { d: "M-17 2 V-14 A17 17 0 0 1 17 -14 V2", fill: "none", stroke: INK, "stroke-width": 8, "stroke-linecap": "round" }, lock);
    S("rect", { x: -30, y: -4, width: 60, height: 48, rx: 11, fill: INK }, lock);
    S("circle", { cx: 0, cy: 15, r: 6, fill: "#fff" }, lock);
    S("rect", { x: -2.5, y: 16, width: 5, height: 12, rx: 2.5, fill: "#fff" }, lock);
    const field = S("g", {}, g);
    S("rect", { x: -80, y: -22, width: 160, height: 44, rx: 22, fill: "#F2F2F7" }, field);
    const dots = [0, 1, 2, 3, 4, 5].map((i) => S("circle", { cx: -50 + i * 20, cy: 0, r: 6, fill: INK, opacity: 0 }, field));
    return (u, sec) => {
      attr(lock, "transform", "translate(200 " + (226 + (1 - Math.min(1, pop(sec, 0.04))) * 20) + ") scale(" + pop(sec, 0.04) + ")");
      attr(shackle, "transform", "translate(0 " + lerp(-13, 0, E.outBack(prog(u, 0.44, 0.58))) + ")");
      dots.forEach((d, i) => attr(d, "opacity", u > 0.14 + i * 0.045 ? 1 : 0));
      attr(field, "transform", "translate(200 328) scale(" + pop(sec, 0.08) + ")");
    };
  }

  const FEATURES = {
    trim: [vTrim, "timeline.selection"], shrink: [vCompress, "arrow.down.right.and.arrow.up.left"], gif: [vGif, "photo.on.rectangle"],
    faces: [vFaces, "person.crop.square"], merge: [vMerge, "doc.on.doc"], ocr: [vOcr, "text.viewfinder"], audio: [vAudio, "waveform"],
    resize: [vResize, "arrow.down.forward.and.arrow.up.backward"], lock: [vLock, "lock"],
  };

  // spec: { W, H, t0, step, wipe: {t0, t1, x, y}, end (words out), implode, features[] (keys above), words[], more, copied,
  //         ring: {cx, cy, R, tile}, disc: {cx, cy, r}, text: {x, y, align, size} }
  function Montage(parent, spec) {
    const keys = spec.features, N = keys.length;
    const root = el("div", { class: "abs layer mont" }, parent);
    const field = el("div", { class: "abs layer m-field" }, root);
    const tx = spec.text;
    const eyebrow = el("div", { class: "m-eyebrow" }, root, spec.more);
    const words = spec.words.map((w) => el("div", { class: "m-word" }, root, '<span class="mask"><span>' + w + "</span></span>"));
    for (const n of [eyebrow, ...words]) set(n, { textAlign: tx.align, left: (tx.align === "left" ? tx.x : 0) + "px", width: (tx.align === "left" ? spec.W - tx.x : spec.W) + "px" });
    set(eyebrow, { top: tx.y + "px" });
    words.forEach((w) => set(w, { top: tx.y + tx.gap + "px", fontSize: tx.size + "px" }));
    const sel = el("div", { class: "m-sel" }, root);
    const T = spec.ring.tile;
    set(sel, { width: T + 22 + "px", height: T + 22 + "px", borderRadius: (T + 22) * 0.27 + "px" });
    const tiles = keys.map((k) => {
      const t = el("div", { class: "m-tile" }, root);
      set(t, { width: T + "px", height: T + "px", borderRadius: T * 0.26 + "px" });
      sym(FEATURES[k][1], T * 0.44, BLUE, t);
      return t;
    });
    const disc = el("div", { class: "m-disc" }, root);
    const D = spec.disc.r * 2;
    set(disc, { width: D + "px", height: D + "px", left: spec.disc.cx - spec.disc.r + "px", top: spec.disc.cy - spec.disc.r + "px" });
    const svg = S("svg", { viewBox: "20 20 360 360", width: D, height: D, class: "m-svg" }, disc);
    const defs = S("defs", {}, svg);
    const vigs = keys.map((k) => {
      const g = S("g", { opacity: 0 }, svg);
      return { g, up: FEATURES[k][0](g, defs, spec) };
    });
    const { t0, step } = spec;
    const tAt = (i) => t0 + i * step;
    const spin = (t) => 9 * (t - t0);
    const angle = (i, t) => -90 + (360 / N) * i + spin(t);

    function update(t) {
      const on = t >= spec.wipe.t0;
      set(root, { display: on ? "block" : "none" });
      if (!on) return;
      const w = E.inOutCubic(prog(t, spec.wipe.t0, spec.wipe.t1));
      const far = Math.hypot(Math.max(spec.wipe.x, spec.W - spec.wipe.x), Math.max(spec.wipe.y, spec.H - spec.wipe.y));
      set(field, { clipPath: w >= 1 ? "none" : "circle(" + (far * w + 0.5) + "px at " + spec.wipe.x + "px " + spec.wipe.y + "px)" });
      const live = t >= t0 - 0.12;
      const cur = clamp(Math.floor((t - t0) / step), 0, N - 1);
      // eyebrow + words (slot machine)
      const eIn = E.outCubic(prog(t, t0 - 0.12, t0 + 0.25)), eOut = E.inCubic(prog(t, spec.end, spec.end + 0.25));
      set(eyebrow, { display: live && eOut < 1 ? "block" : "none", opacity: eIn * (1 - eOut), transform: "translateY(" + ((1 - eIn) * 14 - eOut * 20) + "px)" });
      words.forEach((wd, i) => {
        const ti = tAt(i), tn = i < N - 1 ? tAt(i + 1) : spec.end;
        const a = E.outExpo(prog(t, ti, ti + 0.32));
        const b = i < N - 1 ? E.outExpo(prog(t, tn, tn + 0.32)) : E.inCubic(prog(t, tn, tn + 0.22));
        set(wd, { display: live && t >= ti - 0.01 && t <= tn + 0.33 ? "block" : "none" });
        set(wd.firstChild.firstChild, { transform: "translateY(" + ((1 - a) * 110 - b * 110) + "%)" });
      });
      // tiles on the ring
      const { cx, cy, R } = spec.ring;
      tiles.forEach((tile, i) => {
        const ti = tAt(i);
        const p = spring(t - ti, 0.38, 0.58);
        const k = E.inCubic(prog(t, spec.implode + 0.028 * i, spec.implode + 0.028 * i + 0.42));
        const vis = live && t >= ti && k < 1;
        set(tile, { display: vis ? "flex" : "none" });
        if (!vis) return;
        const act = i === cur && t < spec.implode ? spring(t - ti, 0.3, 0.7) : i === cur - 1 ? 1 - spring(t - tAt(cur), 0.3, 0.7) : 0;
        const [x, y] = polar(cx, cy, R * (1 - k), angle(i, t) + 150 * k);
        const s = Math.max(0.001, p) * lerp(0.92, 1.08, act) * (1 - 0.85 * k);
        set(tile, { transform: "translate(" + (x - T / 2) + "px," + (y - T / 2) + "px) scale(" + s + ") rotate(" + (1 - Math.min(1, p)) * -14 + "deg)",
          opacity: clamp(p * 3) * (1 - clamp(prog(k, 0.7, 1))) });
      });
      // the white selection ring hops from tile to tile
      {
        const hop = spring(t - tAt(cur), 0.3, 0.72);
        const a0 = angle(Math.max(cur - 1, 0), t), a1 = angle(cur, t);
        const [x, y] = polar(cx, cy, R, lerp(a0, a1, cur === 0 ? 1 : hop));
        const sIn = spring(t - t0 - 0.05, 0.36, 0.7), sOut = E.inCubic(prog(t, spec.implode - 0.1, spec.implode + 0.08));
        const S_ = T + 22;
        set(sel, { display: live && sOut < 1 ? "block" : "none", transform: "translate(" + (x - S_ / 2) + "px," + (y - S_ / 2) + "px) scale(" + Math.max(0.001, sIn) * lerp(1.08, 1.16, 1 - sOut) + ")", opacity: clamp(sIn * 2) * (1 - sOut) });
      }
      // centre disc + vignette carousel
      const dIn = spring(t - t0 + 0.06, 0.42, 0.62), dOut = E.inCubic(prog(t, spec.implode + 0.18, spec.implode + 0.5));
      set(disc, { display: live && dOut < 1 ? "block" : "none", transform: "scale(" + Math.max(0.001, dIn * (1 - dOut)) + ")" });
      const slide = E.outExpo(prog(t, tAt(cur), tAt(cur) + 0.34));
      vigs.forEach((v, j) => {
        let x = null;
        if (j === cur) x = cur === 0 ? 0 : lerp(420, 0, slide);
        else if (j === cur - 1 && slide < 1) x = lerp(0, -420, slide);
        if (x === null || !live) { attr(v.g, "opacity", 0); attr(v.g, "visibility", "hidden"); return; }
        attr(v.g, "visibility", "visible");
        attr(v.g, "opacity", 1);
        attr(v.g, "transform", "translate(" + x + " 0)");
        const sec = j === cur ? t - tAt(j) : step;
        v.up(clamp(sec / step), sec);
      });
    }
    return { update };
  }

  PV.Montage = Montage;
})();

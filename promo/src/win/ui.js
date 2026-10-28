// Windows UI for the promo stage (the Windows cut). The wheel and result cards follow the Windows app
// (windows/src/Pickverter/UI: WheelCanvas.cs, Cards.cs, Theme.cs, RingGeometry.cs); around them sits Windows 11 itself:
// File Explorer, the share sheet, a messenger window, an Android phone and, for the Store cut, a Windows notification.
// Built in UI points and scaled by K.
(function () {
  const { el, set, attr, lerp, clamp, prog, E, spring, lit, polar, ringCore, arcPath, mix } = PV;
  const K = 1.3;
  const ACCENT = "#005FB8"; // Theme.Accent, light theme (the default accent's Dark 1)
  const BRAND = "#0A84FF"; // the app icon's picked slot
  const INK = "#1A1A1A";
  const T = (key, ...args) => PV.t(key, ...args);

  // ---------------------------------------------------------------- glyphs (Fluent UI System Icons)
  function glyphOf(name) {
    const g = PV.GLYPHS[name] || PV.GLYPHS.circle;
    return { filled: !!g[0], ch: String.fromCodePoint(g[1]) };
  }
  function gl(name, size, color, extra) {
    const g = glyphOf(name);
    return '<span class="gl' + (g.filled ? " fill" : "") + '" style="font-size:' + size + "px;width:" + size + "px;height:" + size + "px;line-height:" + size + "px;" +
      (color ? "color:" + color + ";" : "") + (extra || "") + '">' + g.ch + "</span>";
  }
  function glyphNode(name, size, color, parent) {
    const n = el("div", { style: { display: "flex" } }, parent, gl(name, size, color));
    return n;
  }

  // Flat document icons (no app logos): a page with a folded corner and a coloured format tag, Windows 11 style.
  const KINDS = { docx: ["#2B579A", "DOCX"], pdf: ["#C4314B", "PDF"], mp4: ["#7A4FB5", "MP4"] };
  function fileIcon(kind, size) {
    const k = KINDS[kind];
    let lines = "";
    for (let i = 0; i < 4; i++) lines += '<rect x="30" y="' + (31 + i * 8.5) + '" width="' + (i === 3 ? 24 : 38) + '" height="3.2" rx="1.6" fill="#D6D6D6"/>';
    return '<svg class="fileicon" width="' + size + '" height="' + size + '" viewBox="0 0 96 96">' +
      '<path d="M24 6.5 H60.5 L79.5 25.5 V86 Q79.5 89.5 76 89.5 H24 Q20.5 89.5 20.5 86 V10 Q20.5 6.5 24 6.5 Z" fill="#FFFFFF" stroke="#BFBFBF" stroke-width="1.4"/>' +
      '<path d="M60.5 6.5 V22 Q60.5 25.5 64 25.5 H79.5" fill="#F0F0F0" stroke="#BFBFBF" stroke-width="1.4" stroke-linejoin="round"/>' + lines +
      '<rect x="8" y="57" width="52" height="25" rx="4" fill="' + k[0] + '"/>' +
      '<text x="34" y="74.5" text-anchor="middle" font-family="PV Segoe, sans-serif" font-weight="600" font-size="' + (k[1].length > 3 ? 13 : 14.5) + '" fill="#fff" letter-spacing="0.3">' + k[1] + "</text></svg>";
  }

  // ---------------------------------------------------------------- cursor: the Windows arrow (white, black outline)
  const ARROW = [[0, 0], [0, 0.842], [0.21, 0.632], [0.368, 1.0], [0.474, 0.947], [0.316, 0.579], [0.579, 0.579]];
  function Cursor(parent) {
    const h = 31;
    const pts = ARROW.map(([x, y]) => (x * h + 3).toFixed(2) + "," + (y * h + 3).toFixed(2)).join(" ");
    const ring = el("div", { class: "abs click-ring" }, parent);
    const root = el("div", { class: "abs cursor" }, parent,
      '<svg width="40" height="44" viewBox="0 0 40 44"><polygon points="' + pts + '" fill="#fff" stroke="#000" stroke-width="1.7" stroke-linejoin="miter" stroke-miterlimit="6"/></svg>');
    function update(s) {
      const press = s.press || 0;
      set(root, { transform: "translate(" + (s.x - 3) + "px," + (s.y - 3) + "px) scale(" + (1 - 0.08 * press) + ")", opacity: s.opacity ?? 1 });
      const r = s.ring || 0;
      set(ring, {
        transform: "translate(" + (s.x - 30) + "px," + (s.y - 30) + "px) scale(" + lerp(0.3, 1.25, E.outCubic(r)) + ")",
        opacity: r > 0 && r < 1 ? (1 - r) * 0.55 * (s.opacity ?? 1) : 0,
      });
    }
    return { update };
  }

  // ---------------------------------------------------------------- keycaps (Windows keys carry words)
  function Keycap(parent, label, opts) {
    opts = opts || {};
    const root = el("div", { class: "abs keycap" + (opts.big ? " big" : "") + (opts.wide ? " wide" : "") + (opts.letter ? " letter" : "") }, parent, label);
    const w = opts.big ? 168 : opts.wide ? 128 : 96, h = opts.big ? 168 : 96;
    function update(s) {
      const pop = s.show;
      const depth = opts.big ? 1.6 : 1;
      set(root, {
        display: pop > 0.001 ? "flex" : "none",
        transform: "translate(" + (s.x - w / 2) + "px," + (s.y - h / 2 + (1 - pop) * 26 + s.press * 4 * depth) + "px) scale(" + lerp(0.7, 1, pop) * (s.scale ?? 1) + ")",
        opacity: clamp(pop * 1.4),
        boxShadow: "0 " + (5 - s.press * 4) * depth + "px 0 #C6CAD0, 0 " + (14 - s.press * 8) * depth + "px " + 26 * depth + "px rgba(0,0,0," + (0.14 - s.press * 0.06) + ")",
      });
    }
    return { update, root, w, h };
  }

  // ---------------------------------------------------------------- File Explorer (Windows 11, light)
  const XWIN = { w: 960, h: 486, side: 196, x0: 214, y0: 147, col: 132, row: 148, cols: 5 };
  function Explorer(parent, files, x, y, C, opts) {
    const win = Object.assign({}, XWIN, opts || {});
    const root = el("div", { class: "abs explorer" }, parent);
    set(root, { left: x + "px", top: y + "px", width: win.w + "px", height: win.h + "px", transform: "scale(" + K + ")", transformOrigin: "0 0" });
    const tabs = el("div", { class: "x-tabs" }, root);
    el("div", { class: "x-tab" }, tabs, gl("fill.desktop", 16, "#1F8AD2") + '<span class="t">' + C.desktop + "</span>" + gl("f.close", 12, "#5F5F5F"));
    el("div", { class: "x-newtab" }, tabs, gl("f.add", 16, "#3B3B3B"));
    el("div", { class: "x-caption" }, tabs, ["f.min", "f.max", "f.close"].map((g) => "<div>" + gl(g, 13, INK) + "</div>").join(""));
    const bar = el("div", { class: "x-bar" }, root);
    for (const [g, c] of [["f.back", INK], ["f.forward", "#A3A3A3"], ["f.up", INK], ["f.refresh", INK]]) el("div", { class: "x-nav" }, bar, gl(g, 16, c));
    el("div", { class: "x-addr" }, bar, gl("fill.desktop", 15, "#1F8AD2") + gl("f.chevron", 10, "#6B6B6B") + "<span>" + C.desktop + "</span>" + gl("f.chevron", 10, "#6B6B6B"));
    el("div", { class: "x-search" }, bar, "<span>" + C.search + "</span>" + gl("f.search", 15, "#3B3B3B"));
    const cmd = el("div", { class: "x-cmd" }, root);
    el("div", { class: "x-btn" }, cmd, gl("f.new", 17, ACCENT) + "<span>" + C.new + "</span>" + gl("f.chevron.down", 10, "#6B6B6B"));
    el("div", { class: "x-sep" }, cmd);
    for (const g of ["f.cut", "f.copy", "f.paste", "f.rename", "f.share", "f.delete"]) el("div", { class: "x-btn" }, cmd, gl(g, 17, "#3B3B3B"));
    el("div", { class: "x-sep" }, cmd);
    el("div", { class: "x-btn" }, cmd, gl("f.sort", 17, "#3B3B3B") + "<span>" + C.sort + "</span>" + gl("f.chevron.down", 10, "#6B6B6B"));
    el("div", { class: "x-btn" }, cmd, gl("f.view", 17, "#3B3B3B") + "<span>" + C.view + "</span>" + gl("f.chevron.down", 10, "#6B6B6B"));
    el("div", { class: "x-sep" }, cmd);
    el("div", { class: "x-btn" }, cmd, gl("f.more", 17, "#3B3B3B"));
    el("div", { class: "x-btn x-details" }, cmd, gl("f.details", 16, "#3B3B3B") + "<span>" + C.details + "</span>");
    const side = el("div", { class: "x-side" }, root);
    // Quick access folders carry a pin, like File Explorer's navigation pane.
    const nav = [["fill.home", "#1F8AD2", C.home], ["fill.gallery", "#1F8AD2", C.gallery], null, ["fill.desktop", "#1F8AD2", C.desktop, true, true],
      ["fill.download", "#2E9E59", C.downloads, false, true], ["fill.document", "#5C7591", C.documents, false, true], ["fill.image", "#1F8AD2", C.pictures, false, true],
      ["fill.music", "#E3732F", C.music, false, true], ["fill.video", "#8661C5", C.videos, false, true], null, ["f.pc", "#3A6EA5", C.pc]];
    for (const n of nav) {
      if (!n) { el("div", { class: "x-div" }, side); continue; }
      el("div", { class: "x-item" + (n[3] ? " on" : "") }, side, gl(n[0], 16, n[1]) + "<span>" + n[2] + "</span>" + (n[4] ? gl("f.pin", 12, "#8A8A8A", "margin-left:auto") : ""));
    }
    const grid = el("div", { class: "x-grid" }, root);
    const tiles = {};
    files.forEach((f, i) => {
      const c = f.slot ?? i;
      const col = c % win.cols, row = Math.floor(c / win.cols);
      const tile = el("div", { class: "x-tile" }, grid);
      set(tile, { left: win.x0 + col * win.col + "px", top: win.y0 + row * win.row + "px" });
      const hl = el("div", { class: "x-hl" }, tile);
      const box = el("div", { class: "x-thumbbox" }, tile);
      let img;
      if (f.photo) {
        img = el("div", { class: "x-photo" }, box);
        set(img, { backgroundImage: "url(" + f.photo + ")", width: f.w + "px", height: f.h + "px" });
        if (f.video) el("div", { style: { position: "absolute", left: "50%", top: "50%", width: "26px", height: "26px", margin: "-13px 0 0 -13px", borderRadius: "50%", background: "rgba(0,0,0,0.45)", display: "flex", alignItems: "center", justifyContent: "center" } }, img, gl("fill.play", 13, "#fff"));
      } else {
        img = el("div", { style: { display: "flex" } }, box, fileIcon(f.kind, 86));
      }
      const label = el("div", { class: "x-label" }, tile, f.name);
      tiles[f.name] = { tile, box, hl, img, label, col, row, f };
    });
    const marquee = el("div", { class: "x-marquee" }, grid);
    const status = el("div", { class: "x-status" }, root);
    const sCount = el("span", {}, status), sSel = el("span", {}, status);
    el("div", { class: "r" }, status, gl("f.details", 14, "#6B6B6B") + gl("f.tiles", 14, ACCENT));
    const toStage = (lx, ly) => [x + lx * K, y + ly * K];
    function at(name) {
      const t = tiles[name];
      return toStage(win.x0 + t.col * win.col + 58, win.y0 + t.row * win.row + 50);
    }
    function box(name) {
      const t = tiles[name];
      const l = win.x0 + t.col * win.col, tp = win.y0 + t.row * win.row;
      return { l, t: tp, r: l + 116, b: tp + 138 };
    }
    function update(s) {
      for (const name in tiles) {
        const t = tiles[name];
        let sel = 0;
        if (s.sel && name in s.sel) sel = s.sel[name];
        set(t.hl, { opacity: sel });
        const appear = s.appear && name in s.appear ? s.appear[name] : 1;
        set(t.tile, { opacity: clamp(appear * 2), transform: "scale(" + lerp(0.4, 1, appear) + ")", display: appear > 0.001 ? "block" : "none" });
        if (t.f.photo) set(t.img, { transform: "rotate(" + ((s.rotate && s.rotate[name]) || 0) + "deg)" });
      }
      const m = s.marquee;
      if (m && m.o > 0.001) {
        set(marquee, { display: "block", left: Math.min(m.x0, m.x1) + "px", top: Math.min(m.y0, m.y1) + "px",
          width: Math.abs(m.x1 - m.x0) + "px", height: Math.abs(m.y1 - m.y0) + "px", opacity: m.o });
      } else set(marquee, { display: "none" });
      if (s.status) {
        if (sCount.textContent !== s.status[0]) sCount.textContent = s.status[0];
        if (sSel.textContent !== s.status[1]) sSel.textContent = s.status[1];
      }
    }
    return { root, at, box, toStage, update, win };
  }

  // ---------------------------------------------------------------- wheel (WheelCanvas.cs, WheelGeometry.cs)
  const G = { outer: 150, inner: 62, rim: 8, subGap: 6, subThick: 54, margin: 22, tabW: 96, tabH: 28, tabSp: 2, tabPad: 3, tabGap: 12 };
  G.disc = G.outer + G.rim; G.subInner = G.disc + G.subGap; G.subOuter = G.subInner + G.subThick; G.box = (G.subOuter + G.margin) * 2;
  G.dead = G.inner * 0.62; G.hub = G.inner - 5;
  const subSpan = (m) => Math.max(22, Math.min(32, 172 / Math.max(m, 1)));
  const PAL = { disc: "#F9F9F9", seg: "#EEEEEE", hair: "rgba(0,0,0,0.09)", label: "rgba(0,0,0,0.79)", tick: "rgba(0,0,0,0.18)" };

  function labelHTML(item, compact) {
    if (item.format) {
      const size = item.title.length > 4 ? 12.5 : 14;
      return '<div class="w-fmt" style="font-size:' + size + 'px">' + item.title + "</div>";
    }
    return '<div class="w-tool">' + gl(item.symbol, compact ? 13 : 16, "currentColor") +
      '<div class="w-tt" style="font-size:' + (compact ? 10 : 11) + 'px">' + item.title + "</div></div>";
  }
  const preferred = (it) => {
    if (!it.variants || it.variants.length < 2) return null;
    const i = it.variants.findIndex((v) => v.default);
    return i >= 0 ? i : 0;
  };

  // A wheel session: opens at the cursor, follows it like the app does, closes on a pick.
  // spec: { id, at:[x,y], open, close, modes:[{t, ring}], choice:{t, ring}, choiceOption, cursor(t), file:{name, thumb|icon, count},
  //         preview(item), bloom, shock, pick }
  function Wheel(parent, rings, spec) {
    const root = el("div", { class: "abs wheel" }, parent);
    const box = G.box, c = box / 2;
    set(root, { width: box + "px", height: box + "px", left: spec.at[0] - c + "px", top: spec.at[1] - c + "px", transformOrigin: c + "px " + c + "px" });
    const svg = el("svg", { width: box, height: box, viewBox: "0 0 " + box + " " + box, class: "abs" }, root);
    const defs = el("defs", {}, svg);
    const blurF = el("filter", { id: "wb" + spec.id, x: "-30%", y: "-30%", width: "160%", height: "160%" }, defs);
    el("feGaussianBlur", { stdDeviation: 11 }, blurF);
    el("circle", { cx: c, cy: c + 5, r: G.disc, fill: "#000", opacity: 0.17, filter: "url(#wb" + spec.id + ")" }, svg);
    const subLayer = el("g", {}, svg);
    el("circle", { cx: c, cy: c, r: G.disc + 0.5, fill: PAL.hair }, svg);
    el("circle", { cx: c, cy: c, r: G.disc - 0.5, fill: PAL.disc }, svg);
    const shock = spec.shock ? el("circle", { cx: c, cy: c, r: G.disc, fill: "none", stroke: ACCENT, opacity: 0 }, svg) : null;
    const labels = el("div", { class: "abs", style: { width: box + "px", height: box + "px" } }, root);

    const ringNodes = {};
    for (const key of Object.keys(rings)) {
      const items = rings[key];
      const g = el("g", {}, svg);
      const lab = el("div", { class: "abs", style: { width: box + "px", height: box + "px" } }, labels);
      const n = items.length, span = 360 / n;
      const segs = items.map((item, i) => {
        const mid = -90 + span * i;
        const sg = el("g", {}, g);
        const d = ringCore(c, c, mid - span / 2, mid + span / 2, G.inner, G.outer, 3, 8);
        const p = el("path", { d, "stroke-linejoin": "round", "stroke-width": 16 }, sg);
        let tick = null;
        if (item.variants && item.variants.length > 1) {
          const [tx, ty] = polar(c, c, G.outer - 7, mid);
          tick = el("rect", { x: tx - 5, y: ty - 1, width: 10, height: 2, rx: 1, transform: "rotate(" + (mid + 90) + " " + tx + " " + ty + ")" }, sg);
        }
        const lr = (G.inner + G.outer) / 2 + 2;
        const [lx, ly] = polar(c, c, lr, mid);
        const arc = (2 * Math.PI * lr) / n;
        const w = Math.max(Math.min(arc - 12, G.outer - G.inner - 8), 40);
        const label = el("div", { class: "abs w-label" }, lab, labelHTML(item));
        set(label, { left: lx - w / 2 + "px", top: ly - 30 + "px", width: w + "px", height: "60px" });
        return { sg, p, tick, label, mid, item, lx, ly };
      });
      const subs = items.map((item, i) => {
        if (!item.variants || item.variants.length < 2) return null;
        const sg = el("g", {}, subLayer);
        const sl = el("div", { class: "abs", style: { width: box + "px", height: box + "px" } }, labels);
        const mid = -90 + span * i;
        const m = item.variants.length, ss = subSpan(m);
        const first = mid - (ss * m) / 2;
        // RingGeometry.Band: Segment(start, end, inner, outer, gap 0, corner 10), outlined by a hairline.
        const band = ringCore(c, c, first - 1.2, first + ss * m + 1.2, G.subInner - 4, G.subOuter + 4, 0, 10);
        el("path", { d: band, fill: PAL.hair, stroke: PAL.hair, "stroke-width": 22, "stroke-linejoin": "round" }, sg);
        el("path", { d: band, fill: PAL.disc, stroke: PAL.disc, "stroke-width": 20, "stroke-linejoin": "round" }, sg);
        const tiles = item.variants.map((v, j) => {
          const start = first + ss * j;
          const d = ringCore(c, c, start, start + ss, G.subInner, G.subOuter, 3, 7);
          const o = v.default ? el("path", { d, fill: ACCENT, stroke: ACCENT, "stroke-width": 16.6, "stroke-linejoin": "round", opacity: 0.7 }, sg) : null;
          const p = el("path", { d, "stroke-linejoin": "round", "stroke-width": 14 }, sg);
          const lr = (G.subInner + G.subOuter) / 2;
          const [lx, ly] = polar(c, c, lr, start + ss / 2);
          const label = el("div", { class: "abs w-label" }, sl, labelHTML(v, true));
          set(label, { left: lx - 36 + "px", top: ly - 30 + "px", width: "72px", height: "60px" });
          return { p, o, label };
        });
        return { g: sg, l: sl, tiles, first, last: first + ss * m, ss };
      });
      ringNodes[key] = { g, lab, segs, subs, n, span, items };
    }

    const hub = el("div", { class: "abs w-hub" }, root);
    set(hub, { left: c - G.hub + "px", top: c - G.hub + "px", width: G.hub * 2 + "px", height: G.hub * 2 + "px" });
    const hubArc = el("svg", { class: "abs", width: G.hub * 2, height: G.hub * 2, style: { left: 0, top: 0 } }, hub);
    const arc = el("path", { fill: "none", stroke: ACCENT, "stroke-width": 2.5, "stroke-linecap": "round" }, hubArc);
    const hubBody = el("div", { class: "w-hubbody" }, hub);
    const hubCache = {};

    const tabs = el("div", { class: "abs w-tabs" }, root);
    const tabH = G.tabH + G.tabPad * 2;
    const pill = el("div", { class: "w-pill" }, tabs);
    const modes = [["convert", T("key.shift")], ["tools", T("key.ctrl")], ["actions", T("key.alt")]];
    const tabLabels = modes.map(([m, key]) => el("div", { class: "w-tab" }, tabs, '<span class="g">' + key + "</span><span class='t'>" + T("mode." + m) + "</span>"));
    // WheelCanvas.TabWidthFor: each tab fits its key and ring name in the current language (96–136).
    function layoutTabs() {
      const w = G.tabW, barW = w * 3 + G.tabSp * 2 + G.tabPad * 2;
      set(tabs, { left: c - barW / 2 + "px", top: c + G.disc + G.tabGap + "px", width: barW + "px", height: tabH + "px" });
      set(pill, { width: w + "px" });
      tabLabels.forEach((tl, i) => set(tl, { left: G.tabPad + i * (w + G.tabSp) + "px", width: w + "px" }));
    }
    layoutTabs();
    (PV.afterFonts = PV.afterFonts || []).push(() => {
      if (!G.measured) {
        const ctx = document.createElement("canvas").getContext("2d");
        let widest = 0;
        for (const [m, key] of modes) {
          ctx.font = "500 10.5px 'PV Segoe', 'PV Malgun', sans-serif";
          const k = ctx.measureText(key).width;
          ctx.font = "600 12px 'PV Segoe', 'PV Malgun', sans-serif";
          widest = Math.max(widest, k + 5 + ctx.measureText(T("mode." + m)).width + 24);
        }
        G.tabW = Math.min(136, Math.max(96, Math.ceil(widest)));
        G.measured = true;
      }
      layoutTabs();
    });
    const keys = [T("key.enter"), T("key.ctrl") + "+C", T("key.ctrl") + "+S", T("key.esc")].join(" · ");
    const caption = el("div", { class: "abs w-choicecap" }, root, T("dest.title") + "<span>" + keys + "</span>");
    set(caption, { left: c - 150 + "px", top: c + G.disc + G.tabGap + 2 + "px", width: "300px", height: "30px" });

    // Hover history, sampled from the cursor like the app's hit test (WheelModel.HitTest).
    function ringAt(t) {
      let key = spec.modes[0].ring;
      for (const m of spec.modes) if (t >= m.t) key = m.ring;
      if (spec.choice && t >= spec.choice.t) key = spec.choice.ring;
      return key;
    }
    const slotEvents = [], subEvents = [], subOpen = [];
    let prev = { key: null, slot: null, sub: null, parent: null };
    const STEP = 1 / 240;
    for (let t = spec.open; t <= spec.close + 1e-6; t += STEP) {
      const key = ringAt(t);
      const R = ringNodes[key];
      const [px, py] = spec.cursor(t);
      const dx = (px - spec.at[0]) / K, dy = (py - spec.at[1]) / K;
      const dist = Math.hypot(dx, dy);
      const a12 = ((Math.atan2(dy, dx) * 180) / Math.PI + 90 + 720) % 360;
      const deg = a12 - 90;
      let slot = prev.key === key ? prev.slot : null;
      let parent = prev.key === key ? prev.parent : null;
      let sub = null;
      if (dist > G.disc + 1 && parent != null && R.subs[parent]) {
        const s = R.subs[parent];
        let d = deg;
        while (d < s.first - 180) d += 360;
        while (d > s.first + 180) d -= 360;
        if (d >= s.first && d <= s.last) sub = Math.min(s.tiles.length - 1, Math.floor((d - s.first) / s.ss));
        slot = parent;
      } else if (dist > G.dead) {
        slot = Math.floor(((a12 + R.span / 2) % 360) / R.span) % R.n;
        parent = R.subs[slot] ? slot : null;
      } else {
        slot = null;
      }
      const slotKey = key + ":" + slot;
      if (!prev.slotKey || prev.slotKey !== slotKey) slotEvents.push({ t, i: slotKey, key, slot });
      const subKey = key + ":" + parent + ":" + sub;
      if (prev.subKey !== subKey) subEvents.push({ t, i: subKey, sub, parent, key });
      const openKey = key + ":" + parent;
      if (prev.openKey !== openKey) subOpen.push({ t, i: openKey });
      prev = { key, slot, parent, sub, slotKey, subKey, openKey };
    }
    const ticks = [];
    for (const ev of slotEvents) if (ev.slot != null && ev.t > spec.open + 0.05 && ev.t < spec.close) ticks.push(ev.t);
    for (const ev of subEvents) if (ev.sub != null && ev.t < spec.close) ticks.push(ev.t);

    function stateAt(t) {
      const key = ringAt(t);
      let slot = null, parent = null, sub = null;
      for (const ev of slotEvents) if (t >= ev.t) slot = ev.key === key ? ev.slot : null;
      for (const ev of subEvents) if (t >= ev.t && ev.key === key) { sub = ev.sub; parent = ev.parent; }
      return { key, slot, parent, sub };
    }

    function idleHTML() {
      const f = spec.file;
      const count = f.count || 1;
      const thumb = f.icon ? '<div class="h-thumb">' + fileIcon(f.icon, 44) + "</div>" : '<div class="h-thumb photo" style="background-image:url(' + f.thumb + ')"></div>';
      const art = count > 1 ? '<div class="h-stack"><div class="h-back"></div>' + thumb + '<div class="h-badge">' + count + "</div></div>" : thumb;
      const title = count > 1 ? PV.tp("wheel.items", count) : f.name;
      return art + '<div class="h-name">' + title + '</div><div class="h-hint">' + T("wheel.hint.click") + "</div>";
    }

    // Hub text follows WheelCanvas.DrawHub: format title, or glyph + the option that would run (accent) + context + hint.
    function hubContent(st) {
      const R = ringNodes[st.key];
      let keyName, html;
      if (st.key === "choice" && st.slot != null) {
        const it = R.items[st.slot];
        keyName = "choice" + st.slot;
        html = gl(it.symbol, 18, INK) + '<div class="h-b">' + it.title + '</div><div class="h-s">' + spec.choiceOption + "</div>";
      } else if (st.slot == null) {
        keyName = "idle";
        html = idleHTML();
      } else {
        const it = R.items[st.slot];
        const vi = st.sub != null ? st.sub : preferred(it);
        const v = vi != null && it.variants ? it.variants[vi] : null;
        const implied = st.sub == null && v != null;
        keyName = st.key + st.slot + ":" + st.sub;
        const hint = implied ? "↑↓ " + PV.tp("hint.options", it.variants.length) : it.files ? T("hint.alt_clipboard") : "";
        if (it.format) {
          html = '<div class="h-fmt">' + it.title + "</div>" + (v ? '<div class="h-acc">' + v.title + "</div>" : "") +
            '<div class="h-s">' + spec.preview(it) + "</div>" + (hint ? '<div class="h-hint">' + hint + "</div>" : "");
        } else {
          html = gl((v && v.symbol) || it.symbol, 18, INK) + (v ? '<div class="h-acc">' + v.title + "</div>" : "") +
            '<div class="h-s">' + (v ? it.title : it.detail || "") + "</div>" + (hint ? '<div class="h-hint">' + hint + "</div>" : "");
        }
      }
      if (!hubCache[keyName]) hubCache[keyName] = el("div", { class: "w-hubc" }, hubBody, html);
      for (const k in hubCache) set(hubCache[k], { display: k === keyName ? "flex" : "none" });
    }

    const bloomKey = spec.bloom ? spec.modes[0].ring : null;
    const changes = [...spec.modes.map((m) => ({ t: m.t, ring: m.ring })), ...(spec.choice ? [{ t: spec.choice.t, ring: spec.choice.ring }] : [])];

    function update(t) {
      const visible = t >= spec.open && t <= spec.close + 0.25;
      set(root, { display: visible ? "block" : "none" });
      if (!visible) return;
      const appear = spec.bloom ? spring(t - spec.open, 0.42, 0.6) : spring(t - spec.open, 0.32, 0.78);
      const fadeIn = E.outCubic(prog(t, spec.open, spec.open + (spec.bloom ? 0.1 : 0.16)));
      const out = prog(t, spec.close, spec.close + 0.18);
      const flash = spec.pick ? 1 + 0.025 * Math.sin(Math.PI * prog(t, spec.close - 0.02, spec.close + 0.1)) : 1;
      const scale = (spec.bloom ? 0.78 + 0.22 * appear : 0.9 + 0.1 * appear) * (1 - 0.06 * E.inCubic(out)) * flash;
      set(root, { transform: "scale(" + K * scale + ")", opacity: fadeIn * (1 - E.inQuad(out)) });
      if (shock) {
        const u = prog(t, spec.open + 0.02, spec.open + 0.6);
        attr(shock, "r", lerp(G.disc + 2, G.disc + 66, E.outCubic(u)));
        attr(shock, "stroke-width", lerp(6, 1, u));
        attr(shock, "opacity", u > 0 && u < 1 ? (1 - u) * 0.6 : 0);
      }
      const st = stateAt(t);
      for (const key in ringNodes) {
        let on = 0;
        for (let i = 0; i < changes.length; i++) {
          const ch = changes[i], next = changes[i + 1];
          if (ch.ring !== key) continue;
          const tin = i === 0 ? 1 : E.outQuad(prog(t, ch.t, ch.t + 0.15));
          const tout = next ? 1 - E.outQuad(prog(t, next.t, next.t + 0.15)) : 1;
          if (t >= ch.t || i === 0) on = Math.max(on, Math.min(tin, tout));
        }
        const R = ringNodes[key];
        attr(R.g, "opacity", on);
        set(R.lab, { opacity: on, display: on > 0.001 ? "block" : "none" });
        if (on <= 0.001) { R.subs.forEach((s) => s && (attr(s.g, "opacity", 0), set(s.l, { display: "none" }))); continue; }
        R.segs.forEach((s, i) => {
          if (key === bloomKey) {
            const k = spring(t - spec.open - 0.016 * i, 0.4, 0.62);
            const sc = lerp(0.3, 1, k);
            attr(s.sg, "transform", "translate(" + c + " " + c + ") scale(" + sc + ") translate(" + -c + " " + -c + ")");
            attr(s.sg, "opacity", clamp(k * 2.2));
            set(s.label, { transform: "translate(" + (c - s.lx) * (1 - sc) + "px," + (c - s.ly) * (1 - sc) + "px) scale(" + sc + ")", opacity: clamp(k * 2.2) });
          }
          const hl = lit(slotEvents, key + ":" + i, t, 0.12);
          const dimmed = st.key === key && st.slot === i && st.sub != null;
          const fill = mix(PAL.seg, ACCENT, hl * (dimmed ? 0.22 : 1));
          attr(s.p, "fill", fill);
          attr(s.p, "stroke", fill);
          if (s.tick) attr(s.tick, "fill", hl > 0.5 && !dimmed ? "rgba(255,255,255,0.6)" : PAL.tick);
          set(s.label, { color: hl > 0.5 && !dimmed ? "#fff" : PAL.label });
        });
        R.subs.forEach((s, i) => {
          if (!s) return;
          const show = lit(subOpen, key + ":" + i, t, 0.15);
          attr(s.g, "opacity", show);
          attr(s.g, "transform", "translate(" + c + " " + c + ") scale(" + (0.97 + 0.03 * show) + ") translate(" + -c + " " + -c + ")");
          set(s.l, { opacity: show, display: show > 0.001 ? "block" : "none" });
          s.tiles.forEach((tile, j) => {
            const hl = lit(subEvents, key + ":" + i + ":" + j, t, 0.1);
            const fill = mix(PAL.seg, ACCENT, hl);
            attr(tile.p, "fill", fill);
            attr(tile.p, "stroke", fill);
            if (tile.o) attr(tile.o, "opacity", 0.7 * (1 - hl));
            set(tile.label, { color: hl > 0.5 ? "#fff" : PAL.label });
          });
        });
      }
      hubContent(st);
      if (spec.bloom) set(hub, { transform: "scale(" + lerp(0.55, 1, spring(t - spec.open - 0.05, 0.36, 0.6)) + ")" });
      const [px, py] = spec.cursor(t);
      const ang = (Math.atan2(py - spec.at[1], px - spec.at[0]) * 180) / Math.PI;
      const hr = G.hub - 3.25;
      attr(arc, "d", arcPath(G.hub, G.hub, hr, ang - 14.4, ang + 14.4));
      attr(arc, "opacity", st.slot != null && st.key !== "choice" ? 1 : st.key === "choice" && st.slot != null ? 1 : 0);
      const choiceOn = spec.choice ? E.outQuad(prog(t, spec.choice.t, spec.choice.t + 0.15)) : 0;
      let subShown = 0;
      for (const key in ringNodes) ringNodes[key].subs.forEach((s, i) => { if (s) subShown = Math.max(subShown, lit(subOpen, key + ":" + i, t, 0.15)); });
      const tabsIn = spec.bloom ? E.outCubic(prog(t, spec.open + 0.12, spec.open + 0.35)) : 1;
      set(tabs, { opacity: (1 - choiceOn) * (1 - subShown) * tabsIn });
      set(caption, { opacity: choiceOn });
      const tabOf = (ring) => (ring.startsWith("convert") ? 0 : ring === "tools" ? 1 : 2);
      let from = tabOf(spec.modes[0].ring), to = from, tm = -10;
      for (let i = 1; i < spec.modes.length; i++) {
        if (t >= spec.modes[i].t) { from = tabOf(spec.modes[i - 1].ring); to = tabOf(spec.modes[i].ring); tm = spec.modes[i].t; }
      }
      const pillX = lerp(from, to, spring(t - tm, 0.28, 0.86));
      set(pill, { transform: "translateX(" + (G.tabPad + pillX * (G.tabW + G.tabSp)) + "px)" });
      tabLabels.forEach((tl, i) => tl.classList.toggle("on", Math.round(pillX) === i));
    }

    return { update, ticks, stateAt, slotEvents };
  }

  // ---------------------------------------------------------------- result card (Cards.cs: a 368-wide Windows 11 window)
  // spec: { x, bottom | top, origin, in, done, out, symbol, thumb | icon, runTitle, runSub, doneTitle, doneSub, buttons:[{title, symbol}] }
  // Anchored to a bottom corner it rises into place (the app's default corner); anchored to the top it drops in.
  function Card(parent, spec) {
    const W = 368;
    const fromTop = spec.top != null;
    const root = el("div", { class: "abs card" }, parent);
    set(root, { width: W + "px", top: fromTop ? spec.top + "px" : "auto", transformOrigin: spec.origin || "100% 100%" });
    const row = el("div", { class: "c-row" }, root);
    const lead = el("div", { class: "c-lead" }, row);
    const ringSvg = el("svg", { width: 34, height: 34, viewBox: "0 0 34 34", class: "c-ring" }, lead);
    el("circle", { cx: 17, cy: 17, r: 15.5, fill: "none", stroke: "rgba(0,0,0,0.09)", "stroke-width": 3 }, ringSvg);
    const prog_ = el("path", { fill: "none", stroke: ACCENT, "stroke-width": 3, "stroke-linecap": "round" }, ringSvg);
    const runIcon = el("div", { style: { position: "relative", display: "flex" } }, lead, gl(spec.symbol || "photo", 13, "rgba(0,0,0,0.62)"));
    const thumb = el("div", { class: "c-thumb" }, lead, spec.icon ? fileIcon(spec.icon, 34) : "");
    if (spec.thumb) set(thumb, { backgroundImage: "url(" + spec.thumb + ")" });
    const text_ = el("div", { class: "c-text" }, row);
    const title = el("div", { class: "c-title" }, text_);
    const sub = el("div", { class: "c-sub" }, text_);
    const barTrack = el("div", { class: "c-bar" }, text_);
    const barFill = el("i", {}, barTrack);
    const close = el("div", { class: "c-close" }, row);
    const stopG = el("div", { style: { display: "flex" } }, close, gl("fill.stop", 12, INK));
    const xG = el("div", { style: { display: "flex" } }, close, gl("f.close", 12, "rgba(0,0,0,0.62)"));
    const buttons = el("div", { class: "c-buttons" }, root);
    const btns = spec.buttons.map((b, i) => {
      const btn = el("div", { class: "c-btn" + (i === 0 ? " pro" : "") }, buttons, gl(b.symbol, 14, i === 0 ? "#fff" : INK) + '<span class="lbl">' + b.title + "</span>");
      return btn;
    });
    // Like the app: the row never wraps; when the labels don't fit, the secondary buttons keep only their icons.
    (PV.afterFonts = PV.afterFonts || []).push(() => {
      set(root, { display: "block" });
      if (buttons.scrollWidth > buttons.clientWidth + 1) btns.slice(1).forEach((b) => b.classList.add("icon"));
    });
    function update(t) {
      const visible = t >= spec.in && t <= spec.out + 0.5;
      set(root, { display: visible ? "block" : "none" });
      if (!visible) return;
      const a = spring(t - spec.in, 0.42, 0.82);
      const o = E.inCubic(prog(t, spec.out, spec.out + 0.32));
      const dy = ((1 - a) * 46 + o * 40) * (fromTop ? -1 : 1);
      set(root, { left: spec.x + "px", transform: "translateY(" + dy + "px) scale(" + K + ")", opacity: clamp(a * 2) * (1 - o) });
      if (!fromTop) set(root, { bottom: spec.bottom + "px" });
      const done = t >= spec.done;
      const p = clamp(prog(t, spec.in + 0.05, spec.done - 0.05));
      const e = E.outCubic(p);
      attr(prog_, "d", p > 0.002 ? arcPath(17, 17, 15.5, -90, -90 + 359.9 * e) : "");
      set(ringSvg, { display: done ? "none" : "block" });
      set(runIcon, { display: done ? "none" : "flex" });
      set(barTrack, { display: done ? "none" : "block" });
      set(barFill, { width: e * 100 + "%" });
      set(stopG, { display: done ? "none" : "flex" });
      set(xG, { display: done ? "flex" : "none" });
      const pop = done ? spring(t - spec.done, 0.3, 0.6) : 0;
      set(thumb, { display: done ? "block" : "none", transform: "scale(" + lerp(0.6, 1, pop) + ")" });
      if (title.textContent !== (done ? spec.doneTitle : spec.runTitle)) title.textContent = done ? spec.doneTitle : spec.runTitle;
      if (sub.textContent !== (done ? spec.doneSub : spec.runSub)) sub.textContent = done ? spec.doneSub : spec.runSub;
      const bShow = done ? E.outCubic(prog(t, spec.done + 0.05, spec.done + 0.3)) : 0;
      set(buttons, { opacity: bShow, maxHeight: (done ? 40 * E.outCubic(prog(t, spec.done, spec.done + 0.25)) : 0) + "px" });
    }
    return { update };
  }

  // ---------------------------------------------------------------- the Windows share sheet
  // Fixed layout (UI points) so the stage can aim the cursor before fonts load. tileCenter(i): 0 = nearby device, 1 = Phone Link, 2 = Mail.
  const SHEET = { w: 340, head: 72, sec: 30, row: 96, more: 40 };
  function ShareSheet(parent, spec) {
    const root = el("div", { class: "abs sheet" }, parent);
    el("div", { class: "s-head", style: { height: SHEET.head + "px", boxSizing: "border-box" } }, root,
      fileIcon("pdf", 40) + '<div class="s-file"><b>' + spec.file + "</b><span>" + spec.size + "</span></div>" + gl("f.close", 14, "#3B3B3B"));
    const tile = (row, icon, bg, fg, label) => {
      const t = el("div", { class: "s-tile" }, row);
      const hl = el("div", { class: "s-hl" }, t);
      el("div", { class: "s-ico", style: { background: bg } }, t, gl(icon, 24, fg));
      el("span", {}, t, label);
      return { t, hl };
    };
    el("div", { class: "s-sec", style: { height: SHEET.sec + "px", boxSizing: "border-box" } }, root, spec.nearby);
    const row1 = el("div", { class: "s-row", style: { height: SHEET.row + "px", boxSizing: "border-box" } }, root);
    const tiles = [tile(row1, "f.laptop", "#E6E6E6", INK, spec.device)];
    el("div", { class: "s-sec", style: { height: SHEET.sec + "px", boxSizing: "border-box" } }, root, spec.using);
    const row2 = el("div", { class: "s-row", style: { height: SHEET.row + "px", boxSizing: "border-box" } }, root);
    tiles.push(tile(row2, "f.phonelink", ACCENT, "#fff", spec.phoneLink), tile(row2, "f.mail", "#E6E6E6", INK, spec.mail));
    el("div", { class: "s-more", style: { height: SHEET.more + "px", boxSizing: "border-box" } }, root, spec.more);
    const H = SHEET.head + SHEET.sec * 2 + SHEET.row * 2 + SHEET.more;
    function tileCenter(i) {
      const rowTop = i === 0 ? SHEET.head + SHEET.sec : SHEET.head + SHEET.sec * 2 + SHEET.row;
      const k = i === 0 ? 0 : i - 1;
      return [10 + k * 82 + 38, rowTop + 4 + 8 + 22];
    }
    // s: { x, y (top-left, stage), show, out, hover: {index: amount}, press: {index: amount} }
    function update(s) {
      const vis = s.show > 0.001 && s.out < 0.999;
      set(root, { display: vis ? "block" : "none" });
      if (!vis) return;
      const sc = K * lerp(0.94, 1, s.show) * (1 - 0.04 * s.out);
      set(root, { left: s.x + "px", top: s.y + "px", transform: "translateY(" + ((1 - s.show) * 14 + s.out * 10) + "px) scale(" + sc + ")", opacity: clamp(s.show * 2) * (1 - s.out) });
      tiles.forEach((tl, i) => {
        const h = (s.hover && s.hover[i]) || 0, p = (s.press && s.press[i]) || 0;
        set(tl.hl, { opacity: Math.min(1, h + p * 0.6) });
        set(tl.t, { transform: "scale(" + (1 - 0.04 * p) + ")" });
      });
    }
    return { update, tileCenter, W: SHEET.w, H, iconCenter: [16 + 20, 16 + 20] };
  }

  // ---------------------------------------------------------------- chat window (a Windows 11 messenger, flat)
  function Chat(parent, spec) {
    const root = el("div", { class: "abs chat" }, parent);
    set(root, { width: spec.w + "px", height: spec.h + "px" });
    el("div", { class: "ch-bar" }, root, '<div class="ch-avatar">' + spec.initial + '</div><div class="ch-name">' + spec.name + "<span>" + spec.status + "</span></div>" +
      '<div class="ch-caption">' + ["f.min", "f.max", "f.close"].map((g) => "<div>" + gl(g, 12, INK) + "</div>").join("") + "</div>");
    const body = el("div", { class: "ch-body" }, root);
    const incoming = el("div", { class: "ch-in" }, body, spec.msg);
    const sticker = el("div", { class: "ch-sticker" }, body);
    set(sticker, { backgroundImage: "url(" + spec.sticker + ")", width: spec.sw + "px", height: spec.sh + "px", right: "24px", top: spec.sy + "px" });
    const sent = el("div", { class: "ch-sent" }, body, spec.sent);
    set(sent, { top: spec.sy + spec.sh + 8 + "px" });
    el("div", { class: "ch-field" }, root, '<span class="grow">' + spec.field + "</span>" + gl("f.emoji", 18, "rgba(0,0,0,0.6)") + gl("f.attach", 18, "rgba(0,0,0,0.6)") +
      '<div class="ch-send">' + gl("fill.send", 15, "#fff") + "</div>");
    const BAR = 56;
    function update(s) {
      const show = s.show || 0, out = s.out || 0;
      const vis = show > 0.001 && out < 0.999;
      const dx = s.from === "y" ? 0 : (1 - show) * 140 + out * 180, dy = s.from === "y" ? (1 - show) * 160 + out * 200 : 0;
      set(root, { display: vis ? "block" : "none", left: s.x + "px", top: s.y + "px", transform: "translate(" + dx + "px," + dy + "px) scale(" + lerp(0.96, 1, show) + ")", opacity: clamp(show * 2.5) * (1 - out) });
      const inc = s.incoming || 0;
      set(incoming, { opacity: clamp(inc * 2), transform: "translateY(" + (1 - inc) * 14 + "px) scale(" + lerp(0.85, 1, inc) + ")" });
      set(sticker, { opacity: s.sticker ? 1 : 0 });
      set(sent, { opacity: s.sent || 0 });
    }
    const stickerRect = () => ({ x: spec.w - 24 - spec.sw, y: BAR + spec.sy, w: spec.sw, h: spec.sh });
    return { update, stickerRect, root };
  }

  // ---------------------------------------------------------------- Android phone (flat): lock screen, a PDF viewer and a notification
  const PHONE = { w: 300, h: 620 };
  function Phone(parent, spec) {
    const root = el("div", { class: "abs phone" }, parent);
    const screen = el("div", { class: "ph-screen" }, root);
    const wall = el("div", { class: "ph-wall" }, screen);
    el("div", { class: "ph-clock" }, wall, spec.clock);
    el("div", { class: "ph-date" }, wall, spec.date);
    const viewer = el("div", { class: "ph-viewer" }, screen);
    el("div", { class: "ph-appbar" }, viewer, gl("f.back", 20, "#1B1C1F") + "<span>" + spec.file + "</span>");
    el("div", { class: "ph-gesture" }, viewer);
    const battery = '<svg width="21" height="12" viewBox="0 0 21 12"><rect x="0.75" y="0.75" width="17.5" height="10.5" rx="3.2" fill="none" stroke="currentColor" stroke-width="1.5"/>' +
      '<rect x="2.6" y="2.6" width="11.5" height="6.8" rx="1.6" fill="currentColor"/><rect x="19" y="4" width="1.7" height="4" rx="0.85" fill="currentColor"/></svg>';
    el("div", { class: "ph-status" }, screen, "<span>" + spec.clock + '</span><span class="r">' + gl("fill.wifi", 14, "currentColor") + gl("fill.cell", 14, "currentColor") + battery + "</span>");
    const status = screen.lastChild;
    el("div", { class: "ph-gesture" }, screen);
    const note = el("div", { class: "ph-note" }, screen,
      '<div class="n-head"><span class="n-app">' + gl("fill.download", 12, "#fff") + "</span><span>" + spec.app + " · " + spec.now + "</span></div>" +
      '<div class="n-body"><div class="n-text"><b>' + spec.file + "</b><span>" + spec.received + '</span></div><div class="n-thumb"><div style="height:16px;background:url(' + spec.thumb + ') center/cover"></div></div></div>' +
      '<div class="n-actions"><span>' + spec.open + "</span><span>" + spec.share + "</span></div>");
    el("div", { class: "ph-hole" }, root);
    function update(s) {
      const show = s.show || 0;
      set(root, { display: show > 0.001 ? "block" : "none", transform: "translate(" + (s.x - PHONE.w / 2) + "px," + (s.y - PHONE.h / 2) + "px) scale(" + (s.scale ?? 1) + ")" });
      const v = s.viewer || 0;
      set(viewer, { opacity: v });
      set(status, { color: v > 0.5 ? "#1B1C1F" : "#fff" });
      const n = s.note || 0;
      set(note, { opacity: clamp(n * 2), transform: "translateY(" + (1 - n) * -60 + "px) scale(" + lerp(0.92, 1, n) + ")" });
    }
    // A document page (1 : 1.42) in the viewer, under the app bar; s is the phone's scale.
    function pageRect(x, y, s) {
      s = s ?? 1;
      const w = 236 * s, h = 334 * s;
      return { x: x - w / 2, y: y - h / 2 + 40 * s, w, h };
    }
    return { update, pageRect, root };
  }

  // ---------------------------------------------------------------- Windows notification (toast), the Store cut's ending
  // Phone Link's "sent" notification. A fixed size (UI points), as wide as the result cards, which sit in the same corner;
  // spec takes the cards' anchor: { x, bottom | top, origin, in, out, app, title, text }.
  const TOAST = { w: 368, h: 104 };
  function Toast(parent, spec) {
    const fromTop = spec.top != null;
    const root = el("div", { class: "abs toast" }, parent);
    set(root, { width: TOAST.w + "px", height: TOAST.h + "px", top: fromTop ? spec.top + "px" : "auto", transformOrigin: spec.origin || "100% 100%" });
    el("div", { class: "t-head" }, root,
      '<span class="t-app">' + gl("fill.phonelink", 11, "#fff") + '</span><span class="t-name">' + spec.app + "</span>" +
      '<span class="t-tools">' + gl("f.more", 16, "rgba(0,0,0,0.62)") + gl("f.close", 12, "rgba(0,0,0,0.62)") + "</span>");
    el("div", { class: "t-body" }, root, fileIcon("pdf", 44) + '<div class="t-text"><b>' + spec.title + "</b><span>" + spec.text + "</span></div>");
    function update(t) {
      const visible = t >= spec.in && t <= spec.out + 0.5;
      set(root, { display: visible ? "block" : "none" });
      if (!visible) return;
      const a = spring(t - spec.in, 0.42, 0.82);
      const o = E.inCubic(prog(t, spec.out, spec.out + 0.32));
      // Windows slides a notification in from the screen's edge (from above when it sits at the top, as in 9:16).
      const dx = fromTop ? 0 : (1 - a) * 150 + o * 60, dy = fromTop ? -((1 - a) * 46 + o * 40) : 0;
      set(root, { left: spec.x + "px", transform: "translate(" + dx + "px," + dy + "px) scale(" + K + ")", opacity: clamp(a * 2) * (1 - o) });
      if (!fromTop) set(root, { bottom: spec.bottom + "px" });
    }
    // Its centre on a stage H tall, once in place (scaled by K about its transform origin).
    function center(H) {
      if (fromTop) return [spec.x + TOAST.w / 2, spec.top + (TOAST.h * K) / 2];
      return [spec.x + TOAST.w - (TOAST.w * K) / 2, H - spec.bottom - (TOAST.h * K) / 2];
    }
    return { update, center, root };
  }

  PV.UI = { K, ACCENT, BRAND, INK, G, gl, glyphNode, fileIcon, Cursor, Keycap, Explorer, Wheel, Card, ShareSheet, Chat, Phone, Toast, XWIN, PHONE, SHEET, TOAST };
})();


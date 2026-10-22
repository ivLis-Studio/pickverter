// UI components for the promo stage, drawn to match the app (WheelView.swift, JobCenter.swift).
// Everything is built in UI points and scaled by K, so proportions match the real app.
(function () {
  const { el, sym, set, attr, lerp, clamp, prog, E, spring, lit, polar, ringCore, arcPath, mix } = PV;
  const K = 1.3;
  const ACCENT = "#0A7CFF";
  const INK = "#1D1D1F";
  const T = (key, ...args) => PV.t(key, ...args);

  // ---------------------------------------------------------------- cursor
  const ARROW = [[0, 0], [0, 0.852], [0.198, 0.67], [0.341, 1.0], [0.473, 0.945], [0.33, 0.621], [0.6, 0.621]];
  function Cursor(parent) {
    const h = 33;
    const pts = ARROW.map(([x, y]) => (x * h + 3) + "," + (y * h + 3)).join(" ");
    const ring = el("div", { class: "abs click-ring" }, parent);
    const root = el("div", { class: "abs cursor" }, parent,
      '<svg width="40" height="44" viewBox="0 0 40 44"><polygon points="' + pts + '" fill="#111" stroke="#fff" stroke-width="2.6" stroke-linejoin="round"/></svg>');
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

  // ---------------------------------------------------------------- keycap overlay
  function Keycap(parent, glyph, word, big = false, letter = false) {
    const root = el("div", { class: "abs keycap" + (big ? " big" : "") + (letter ? " letter" : "") }, parent);
    const half = big ? 84 : 48;
    el("div", { class: "kc-glyph" }, root, glyph);
    if (word) el("div", { class: "kc-word" }, root, word);
    function update(s) {
      // s.show 0..1 (pop), s.press 0..1, s.scale (optional size multiplier)
      const pop = s.show;
      const depth = big ? 1.6 : 1;
      set(root, {
        display: pop > 0.001 ? "block" : "none",
        transform: "translate(" + (s.x - half) + "px," + (s.y - half + (1 - pop) * 26 + s.press * 4 * depth) + "px) scale(" + lerp(0.7, 1, pop) * (s.scale ?? 1) + ")",
        opacity: clamp(pop * 1.4),
        boxShadow: "0 " + (5 - s.press * 4) * depth + "px 0 #C8C8CE, 0 " + (14 - s.press * 8) * depth + "px " + 26 * depth + "px rgba(0,0,0," + (0.14 - s.press * 0.06) + ")",
      });
    }
    return { update, root };
  }

  // ---------------------------------------------------------------- Finder window
  const WIN = { w: 780, h: 480, side: 176, bar: 52, col: 134, row: 142, x0: 200, y0: 70, cols: 4 };
  function Finder(parent, files, x, y, opts = {}) {
    const win = Object.assign({}, WIN, opts);
    const root = el("div", { class: "abs finder" }, parent);
    set(root, { left: x + "px", top: y + "px", width: win.w + "px", height: win.h + "px", transform: "scale(" + K + ")", transformOrigin: "0 0" });
    const side = el("div", { class: "f-side" }, root);
    const lights = el("div", { class: "f-lights" }, root);
    ["#FF5F57", "#FEBC2E", "#28C840"].forEach((c) => el("span", { style: { background: c } }, lights));
    const items = [["", T("promo.favorites")], ["dot.radiowaves.left.and.right", "AirDrop"], ["clock", T("promo.recents")],
      ["app.dashed", T("promo.applications")], ["menubar.dock.rectangle", T("folder.desktop"), true], ["doc", T("folder.documents")],
      ["arrow.down.circle", T("folder.downloads")], ["", "iCloud"], ["icloud", T("folder.icloud")]];
    for (const [icon, label, active] of items) {
      if (!icon) { el("div", { class: "f-head" }, side, label); continue; }
      const row = el("div", { class: "f-item" + (active ? " on" : "") }, side);
      sym(icon, 15, ACCENT, row);
      el("span", {}, row, label);
    }
    const bar = el("div", { class: "f-bar" }, root);
    const nav = el("div", { class: "f-nav" }, bar);
    sym("chevron.left", 15, "#3C3C43", nav); sym("chevron.right", 15, "#C7C7CC", nav);
    el("div", { class: "f-title" }, bar, T("folder.desktop"));
    const tools_ = el("div", { class: "f-tools" }, bar);
    for (const s of ["square.grid.2x2", "list.bullet", "square.and.arrow.up", "tag", "ellipsis.circle", "magnifyingglass"]) sym(s, 16, "#4A4A50", el("div", { class: "f-tool" }, tools_));
    const grid = el("div", { class: "f-grid" }, root);
    const tiles = {};
    files.forEach((f, i) => {
      const c = f.slot ?? i;
      const col = c % win.cols, row = Math.floor(c / win.cols);
      const tile = el("div", { class: "f-tile" }, grid);
      set(tile, { left: win.x0 + col * win.col + "px", top: win.y0 + row * win.row + "px" });
      const box = el("div", { class: "f-thumbbox" }, tile);
      const hl = el("div", { class: "f-hl" }, box);
      let img;
      if (f.photo) {
        img = el("div", { class: "f-photo" }, box);
        set(img, { backgroundImage: "url(" + f.photo + ")", width: f.w + "px", height: f.h + "px" });
      } else {
        img = el("img", { src: f.icon, class: "f-icon" }, box);
      }
      const label = el("div", { class: "f-label" }, tile, "<span>" + f.name + "</span>").firstChild;
      tiles[f.name] = { tile, box, hl, img, label, col, row, f };
    });
    const marquee = el("div", { class: "f-marquee" }, grid);
    // Window-local point → stage point.
    const toStage = (lx, ly) => [x + lx * K, y + ly * K];
    // Stage-space centre of a file's thumbnail.
    function at(name) {
      const t = tiles[name];
      return toStage(win.x0 + t.col * win.col + 60, win.y0 + t.row * win.row + 47);
    }
    // Window-local thumbnail box of a file (for marquee hit tests).
    function box(name) {
      const t = tiles[name];
      const l = win.x0 + t.col * win.col + 12, tp = win.y0 + t.row * win.row;
      return { l, t: tp, r: l + 96, b: tp + 94 };
    }
    function update(s) {
      set(root, { opacity: s.opacity ?? 1, transform: "translate(0," + (s.dy || 0) + "px) scale(" + K * (s.scale ?? 1) + ")" });
      const many = Array.isArray(s.selected) ? s.selected : null;
      for (const name in tiles) {
        const t = tiles[name];
        let sel = 0;
        if (s.sel && name in s.sel) sel = s.sel[name];
        else if (many) sel = many.includes(name) ? 1 : 0;
        else if (s.selected === name) sel = s.selAmt ?? 1;
        set(t.hl, { opacity: sel });
        set(t.label, { background: sel > 0.5 ? ACCENT : "transparent", color: sel > 0.5 ? "#fff" : INK });
        const appear = s.appear && name in s.appear ? s.appear[name] : 1;
        set(t.tile, { opacity: clamp(appear * 2), transform: "scale(" + lerp(0.4, 1, appear) + ")", display: appear > 0.001 ? "block" : "none" });
        if (t.f.photo) set(t.img, { transform: "rotate(" + ((s.rotate && s.rotate[name]) || 0) + "deg)" });
        const hidden = s.hideImg && s.hideImg[name] ? s.hideImg[name] : 0;
        if (t.f.photo) set(t.img, { opacity: 1 - hidden });
      }
      const m = s.marquee;
      if (m && m.o > 0.001) {
        set(marquee, { display: "block", left: Math.min(m.x0, m.x1) + "px", top: Math.min(m.y0, m.y1) + "px",
          width: Math.abs(m.x1 - m.x0) + "px", height: Math.abs(m.y1 - m.y0) + "px", opacity: m.o });
      } else set(marquee, { display: "none" });
    }
    return { root, at, box, toStage, update, win };
  }

  // ---------------------------------------------------------------- wheel
  const G = { outer: 150, inner: 62, rim: 8, subGap: 6, subThick: 54, margin: 22, tabW: 94, tabH: 28, tabSp: 2, tabPad: 3, tabGap: 12 };
  G.disc = G.outer + G.rim; G.subInner = G.disc + G.subGap; G.subOuter = G.subInner + G.subThick; G.box = (G.subOuter + G.margin) * 2;
  G.dead = G.inner * 0.62; G.hub = G.inner - 5;
  const SUB_SPAN = 27;

  function labelHTML(item, compact) {
    if (item.format) {
      const size = item.title.length > 4 ? 12.5 : 14;
      return '<div class="w-fmt" style="font-size:' + size + 'px">' + item.title + "</div>";
    }
    const s = compact ? 11.5 : 14;
    return '<div class="w-tool"><div class="sym" style="width:' + (s * 1.35) + "px;height:" + (s * 1.25) + "px;-webkit-mask-image:url(assets/symbols/" + item.symbol + '.png)"></div>' +
      '<div class="w-tt" style="font-size:' + (compact ? 10 : 11) + 'px">' + item.title + "</div></div>";
  }

  // A wheel session: opens at the cursor, follows it like the app does, closes on a pick.
  // spec: { at:[x,y], open, close, modes:[{t, ring}], choice:{t, ring}, cursor(t)->[x,y], file:{name, thumb, count}, preview(item, sub),
  //         bloom (segments fly out on open), shock (a ring flashes on open) }
  function Wheel(parent, rings, spec) {
    // Light/dark palettes follow the app's snapshot colours (Theme.swift).
    const pal = spec.dark
      ? { disc: "#2C2C2F", seg: "#3A3A3D", hair: "rgba(255,255,255,0.14)", label: "rgba(245,245,247,0.88)", tick: "rgba(255,255,255,0.2)", ink: "#F5F5F7" }
      : { disc: "#F2F2F4", seg: "#E7E7EA", hair: "rgba(0,0,0,0.1)", label: "rgba(29,29,31,0.85)", tick: "rgba(0,0,0,0.15)", ink: INK };
    const root = el("div", { class: "abs wheel" + (spec.dark ? " dark" : "") }, parent);
    const box = G.box, c = box / 2;
    set(root, { width: box + "px", height: box + "px", left: spec.at[0] - c + "px", top: spec.at[1] - c + "px", transformOrigin: c + "px " + c + "px" });
    const svg = el("svg", { width: box, height: box, viewBox: "0 0 " + box + " " + box, class: "abs" }, root);
    const defs = el("defs", {}, svg);
    const blur = el("filter", { id: "wb" + spec.id, x: "-30%", y: "-30%", width: "160%", height: "160%" }, defs);
    el("feGaussianBlur", { stdDeviation: 9 }, blur);
    el("circle", { cx: c, cy: c + 6, r: G.disc, fill: "#000", opacity: 0.13, filter: "url(#wb" + spec.id + ")" }, svg);
    const subLayer = el("g", {}, svg);
    el("circle", { cx: c, cy: c, r: G.disc, fill: pal.disc }, svg);
    el("circle", { cx: c, cy: c, r: G.disc - 0.25, fill: "none", stroke: pal.hair, "stroke-width": 0.5 }, svg);
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
        if (item.variants) {
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
      // Outer rings for slots with options.
      const subs = items.map((item, i) => {
        if (!item.variants) return null;
        const sg = el("g", {}, subLayer);
        const sl = el("div", { class: "abs", style: { width: box + "px", height: box + "px" } }, labels);
        const mid = -90 + span * i;
        const m = item.variants.length;
        const first = mid - (SUB_SPAN * m) / 2;
        const band = ringCore(c, c, first - 1.2, first + SUB_SPAN * m + 1.2, G.subInner - 4, G.subOuter + 4, 0, 10);
        el("path", { d: band, fill: pal.disc, stroke: pal.disc, "stroke-width": 20, "stroke-linejoin": "round" }, sg);
        const tiles = item.variants.map((v, j) => {
          const start = first + SUB_SPAN * j;
          const d = ringCore(c, c, start, start + SUB_SPAN, G.subInner, G.subOuter, 3, 7);
          const p = el("path", { d, "stroke-linejoin": "round", "stroke-width": 14 }, sg);
          const o = v.default ? el("path", { d: ringCore(c, c, start, start + SUB_SPAN, G.subInner + 0.6, G.subOuter - 0.6, 4.2, 6.4), fill: "none", stroke: ACCENT, "stroke-opacity": 0.6, "stroke-width": 1.3 }, sg) : null;
          const lr = (G.subInner + G.subOuter) / 2;
          const [lx, ly] = polar(c, c, lr, start + SUB_SPAN / 2);
          const label = el("div", { class: "abs w-label" }, sl, labelHTML(v, true));
          set(label, { left: lx - 34 + "px", top: ly - 30 + "px", width: "68px", height: "60px" });
          return { p, o, label, from: start, to: start + SUB_SPAN };
        });
        return { g: sg, l: sl, tiles, first, last: first + SUB_SPAN * m };
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
    const tabW = G.tabW * 3 + G.tabSp * 2 + G.tabPad * 2, tabH = G.tabH + G.tabPad * 2;
    set(tabs, { left: c - tabW / 2 + "px", top: c + G.disc + G.tabGap + "px", width: tabW + "px", height: tabH + "px" });
    const pill = el("div", { class: "w-pill" }, tabs);
    const modes = [["convert", "⇧"], ["tools", "⌘"], ["actions", "⌃"]];
    const tabLabels = modes.map(([m, glyph], i) => {
      const tl = el("div", { class: "w-tab" }, tabs, '<span class="g">' + glyph + "</span><span class='t'>" + T("mode." + m) + "</span>");
      set(tl, { left: G.tabPad + i * (G.tabW + G.tabSp) + "px" });
      return tl;
    });
    const caption = el("div", { class: "abs w-choicecap" }, root, T("dest.title"));
    set(caption, { left: c - 120 + "px", top: c + G.disc + G.tabGap + "px", width: "240px", height: tabH + "px" });

    // ---- derive hover history by sampling the cursor like the app's hit test.
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
      const a12 = ((Math.atan2(dy, dx) * 180) / Math.PI + 90 + 720) % 360; // clockwise from 12 o'clock
      const swiftDeg = a12 - 90;
      let slot = prev.key === key ? prev.slot : null;
      let parent = prev.key === key ? prev.parent : null;
      let sub = null;
      if (dist > G.disc + 1 && parent != null && R.subs[parent]) {
        const s = R.subs[parent];
        let d = swiftDeg;
        while (d < s.first - 180) d += 360;
        while (d > s.first + 180) d -= 360;
        if (d >= s.first && d <= s.last) sub = Math.min(s.tiles.length - 1, Math.floor((d - s.first) / SUB_SPAN));
        slot = parent;
      } else if (dist > G.dead) {
        slot = Math.floor(((a12 + R.span / 2) % 360) / R.span) % R.n;
        parent = R.items[slot].variants ? slot : null;
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
    // Audible ticks: every newly lit slot or outer option after the wheel has settled.
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
      const count = spec.file.count || 1;
      const thumb = '<div class="h-thumb' + (spec.file.icon ? " icon" : "") + '" style="background-image:url(' + spec.file.thumb + ')"></div>';
      const art = count > 1 ? '<div class="h-stack"><div class="h-back"></div>' + thumb + '<div class="h-badge">' + count + "</div></div>" : thumb;
      const title = count > 1 ? PV.tp("wheel.items", count) : spec.file.name;
      return art + '<div class="h-name">' + title + '</div><div class="h-hint">' + T("wheel.hint.click") + "</div>";
    }

    function hubContent(st) {
      const R = ringNodes[st.key];
      let keyName, html;
      if (st.key === "choice" && st.slot != null) {
        const it = R.items[st.slot];
        keyName = "choice" + st.slot;
        html = '<div class="sym" style="width:22px;height:20px;background:' + pal.ink + ";-webkit-mask-image:url(assets/symbols/" + it.symbol + '.png)"></div>' +
          '<div class="h-b">' + it.title + '</div><div class="h-s">' + spec.choiceOption + "</div>";
      } else if (st.slot == null) {
        keyName = "idle";
        html = idleHTML();
      } else {
        const it = R.items[st.slot];
        const v = st.sub != null ? it.variants[st.sub] : null;
        keyName = st.key + st.slot + ":" + st.sub;
        if (it.format) {
          html = '<div class="h-fmt">' + it.title + '</div><div class="h-s">' + spec.preview(it) + '</div><div class="h-hint">' + T("hint.alt_clipboard") + "</div>";
        } else {
          const symName = v ? v.symbol : it.symbol;
          html = '<div class="sym" style="width:22px;height:20px;background:' + pal.ink + ";-webkit-mask-image:url(assets/symbols/" + symName + '.png)"></div>' +
            (v ? '<div class="h-acc">' + v.title + "</div>" : '<div class="h-b">' + it.title + "</div>") +
            '<div class="h-s">' + (v ? it.title : it.detail || "") + "</div>" +
            (it.variants && !v ? '<div class="h-hint">' + PV.tp("hint.options", it.variants.length) + "</div>" : "");
        }
      }
      if (!hubCache[keyName]) {
        const node = el("div", { class: "w-hubc" }, hubBody, html);
        hubCache[keyName] = node;
      }
      for (const k in hubCache) set(hubCache[k], { display: k === keyName ? "flex" : "none" });
    }

    const bloomKey = spec.bloom ? spec.modes[0].ring : null;

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
      // ring crossfade
      for (const key in ringNodes) {
        let on = 0;
        const changes = [...spec.modes.map((m) => ({ t: m.t, ring: m.ring })), ...(spec.choice ? [{ t: spec.choice.t, ring: spec.choice.ring }] : [])];
        for (let i = 0; i < changes.length; i++) {
          const ch = changes[i];
          const next = changes[i + 1];
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
          const fill = mix(pal.seg, ACCENT, hl * (dimmed ? 0.22 : 1));
          attr(s.p, "fill", fill);
          attr(s.p, "stroke", fill);
          if (s.tick) attr(s.tick, "fill", hl > 0.5 && !dimmed ? "rgba(255,255,255,0.6)" : pal.tick);
          set(s.label, { color: hl > 0.5 && !dimmed ? "#fff" : pal.label });
        });
        R.subs.forEach((s, i) => {
          if (!s) return;
          const show = lit(subOpen, key + ":" + i, t, 0.15);
          attr(s.g, "opacity", show);
          attr(s.g, "transform", "translate(" + c + " " + c + ") scale(" + (0.97 + 0.03 * show) + ") translate(" + -c + " " + -c + ")");
          set(s.l, { opacity: show, display: show > 0.001 ? "block" : "none" });
          s.tiles.forEach((tile, j) => {
            const hl = lit(subEvents, key + ":" + i + ":" + j, t, 0.1);
            const fill = mix(pal.seg, ACCENT, hl);
            attr(tile.p, "fill", fill);
            attr(tile.p, "stroke", fill);
            if (tile.o) attr(tile.o, "opacity", 1 - hl);
            set(tile.label, { color: hl > 0.5 ? "#fff" : pal.label });
          });
        });
      }
      // hub
      hubContent(st);
      if (spec.bloom) set(hub, { transform: "scale(" + lerp(0.55, 1, spring(t - spec.open - 0.05, 0.36, 0.6)) + ")" });
      const [px, py] = spec.cursor(t);
      const ang = (Math.atan2(py - spec.at[1], px - spec.at[0]) * 180) / Math.PI;
      const showArc = st.slot != null ? 1 : 0;
      const hr = G.hub - 2 - 1.25;
      attr(arc, "d", arcPath(G.hub, G.hub, hr, ang - 14.4, ang + 14.4));
      attr(arc, "opacity", showArc);
      // tabs / choice caption
      const choiceOn = spec.choice ? E.outQuad(prog(t, spec.choice.t, spec.choice.t + 0.15)) : 0;
      // Like the app, the tab bar steps aside while an outer ring is open.
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

  // ---------------------------------------------------------------- result card
  // spec.slide: "x" (default, from the right edge) or "y" (drops in from above); spec.origin: CSS transform-origin.
  function Card(parent, spec) {
    const W = 352;
    const root = el("div", { class: "abs card" }, parent);
    set(root, { width: W + "px", transformOrigin: spec.origin || "100% 0" });
    const row = el("div", { class: "c-row" }, root);
    const lead = el("div", { class: "c-lead" }, row);
    const ringSvg = el("svg", { width: 44, height: 44, viewBox: "0 0 44 44", class: "c-ring" }, lead);
    el("circle", { cx: 22, cy: 22, r: 19, fill: "none", stroke: "rgba(0,0,0,0.1)", "stroke-width": 3.5 }, ringSvg);
    const prog_ = el("path", { fill: "none", stroke: ACCENT, "stroke-width": 3.5, "stroke-linecap": "round" }, ringSvg);
    const runIcon = sym(spec.symbol || "photo", 18, "#6E6E73", lead, { position: "absolute", left: "13px", top: "13px" });
    const thumb = el("div", { class: "c-thumb" }, lead);
    if (spec.thumb) set(thumb, { backgroundImage: "url(" + spec.thumb + ")" });
    if (spec.thumbCheck) thumb.classList.add("check");
    if (spec.thumbFit) thumb.classList.add("fit");
    const text_ = el("div", { class: "c-text" }, row);
    const title = el("div", { class: "c-title" }, text_);
    const sub = el("div", { class: "c-sub" }, text_);
    const close = el("div", { class: "c-close" }, row);
    sym("xmark", 11, "#6E6E73", close);
    const buttons = el("div", { class: "c-buttons" }, root);
    const btns = spec.buttons.map((b, i) => {
      const btn = el("div", { class: "c-btn" + (i === 0 ? " pro" : "") }, buttons);
      sym(b.symbol, 14, i === 0 ? "#fff" : INK, btn);
      el("span", {}, btn, b.title);
      return btn;
    });
    // Like the app: when the labels don't fit on one line, secondary buttons keep only their icons.
    (PV.afterFonts = PV.afterFonts || []).push(() => {
      set(root, { display: "block" });
      if (buttons.scrollWidth > buttons.clientWidth + 1) btns.slice(1).forEach((b) => b.classList.add("icon"));
    });
    function update(t) {
      const inT = spec.in, outT = spec.out;
      const visible = t >= inT && t <= outT + 0.5;
      set(root, { display: visible ? "block" : "none" });
      if (!visible) return;
      const a = spring(t - inT, 0.42, 0.8);
      const o = E.inCubic(prog(t, outT, outT + 0.35));
      const move = spec.slide === "y" ? "translateY(" + (-(1 - a) * 80 - o * 70) + "px)" : "translateX(" + ((1 - a) * 90 + o * 120) + "px)";
      set(root, { left: spec.x + "px", top: spec.y + "px", transform: move + " scale(" + K + ")", opacity: clamp(a * 2) * (1 - o) });
      const done = t >= spec.done;
      const p = clamp(prog(t, inT + 0.05, spec.done - 0.05));
      attr(prog_, "d", p > 0.002 ? arcPath(22, 22, 19, -90, -90 + 359.9 * E.outCubic(p)) : "");
      set(ringSvg, { display: done ? "none" : "block" });
      set(runIcon, { display: done ? "none" : "block" });
      const pop = done ? spring(t - spec.done, 0.3, 0.6) : 0;
      set(thumb, { display: done ? "block" : "none", transform: "scale(" + lerp(0.6, 1, pop) + ")" });
      title.textContent = done ? spec.doneTitle : spec.runTitle;
      sub.textContent = done ? spec.doneSub : spec.runSub;
      const bShow = done ? E.outCubic(prog(t, spec.done + 0.05, spec.done + 0.3)) : 0;
      set(buttons, { opacity: bShow, maxHeight: (done ? 44 * E.outCubic(prog(t, spec.done, spec.done + 0.25)) : 0) + "px" });
    }
    return { update };
  }

  // ---------------------------------------------------------------- terminal (kept for older cuts)
  function Terminal(parent, x, y, lines) {
    const root = el("div", { class: "abs term" }, parent);
    set(root, { left: x + "px", top: y + "px" });
    const bar = el("div", { class: "t-bar" }, root);
    const lights = el("div", { class: "f-lights t-lights" }, bar);
    ["#FF5F57", "#FEBC2E", "#28C840"].forEach((c) => el("span", { style: { background: c } }, lights));
    el("div", { class: "t-title" }, bar, lines.title);
    const body = el("div", { class: "t-body" }, root);
    function update(t, s) {
      const visible = t >= s.in && t <= s.out + 0.4;
      set(root, { display: visible ? "block" : "none" });
      if (!visible) return;
      const a = spring(t - s.in, 0.36, 0.78);
      const o = E.inCubic(prog(t, s.out, s.out + 0.3));
      set(root, { transform: "translateY(" + ((1 - a) * 24 + o * 30) + "px) scale(" + K * (0.94 + 0.06 * a) * (1 - 0.03 * o) + ")", opacity: clamp(a * 2.2) * (1 - o) });
      const typed = lines.command.slice(0, Math.floor(clamp((t - s.typeAt) / 0.07, 0, lines.command.length)));
      const showOut = t >= s.outAt;
      const blink = Math.floor(t * 2.2) % 2 === 0;
      const caret = '<span class="t-caret" style="opacity:' + (blink ? 1 : 0) + '"></span>';
      let html = '<div class="t-dim">' + lines.login + "</div>";
      html += "<div>" + lines.prompt + " " + typed + (showOut ? "" : caret) + "</div>";
      if (showOut) html += '<div class="t-out">' + lines.output + "</div><div>" + lines.prompt + " " + caret + "</div>";
      if (body.__h !== html) { body.innerHTML = html; body.__h = html; }
    }
    return { update };
  }

  // ---------------------------------------------------------------- iPhone (flat)
  // Screen-space phone, 300×620. update({ x, y, show, viewer, banner }) — x/y is the centre. spec.viewerBg tints the viewer.
  const PHONE = { w: 300, h: 620, inset: 11 };
  function Phone(parent, spec) {
    const root = el("div", { class: "abs phone" }, parent);
    const screen = el("div", { class: "ph-screen" }, root);
    const wall = el("div", { class: "ph-wall" }, screen);
    el("div", { class: "ph-date" }, wall, spec.date || "");
    el("div", { class: "ph-time" }, wall, "9:41");
    const viewer = el("div", { class: "ph-viewer" }, screen);
    if (spec.viewerBg) set(viewer, { background: spec.viewerBg });
    const banner = el("div", { class: "ph-banner" }, screen);
    const icon = el("div", { class: "ph-bicon" }, banner);
    sym("dot.radiowaves.left.and.right", 17, "#fff", icon);
    el("div", { class: "ph-btext" }, banner, "<b>AirDrop</b><span>" + spec.bannerText + "</span>");
    el("div", { class: "ph-island" }, root);
    function update(s) {
      const show = s.show || 0;
      set(root, { display: show > 0.001 ? "block" : "none", transform: "translate(" + (s.x - PHONE.w / 2) + "px," + (s.y - PHONE.h / 2) + "px) scale(" + (s.scale ?? 1) + ")" });
      set(viewer, { opacity: s.viewer || 0 });
      const bn = s.banner || 0;
      set(banner, { opacity: clamp(bn * 2), transform: "translateY(" + (1 - bn) * -70 + "px) scale(" + lerp(0.9, 1, bn) + ")" });
    }
    // The fitted photo rectangle inside the screen (stage coordinates, for a landscape 3:2 photo).
    function photoRect(x, y) {
      const w = PHONE.w - PHONE.inset * 2, h = w / 1.5;
      return { x: x - w / 2, y: y - h / 2 + 10, w, h };
    }
    // A document page (1 : 1.42) shown in the viewer under the banner; s is the phone's scale.
    function pageRect(x, y, s = 1) {
      const w = 236 * s, h = 334 * s;
      return { x: x - w / 2, y: y - h / 2 + 30 * s, w, h };
    }
    return { update, photoRect, pageRect, root };
  }

  // ---------------------------------------------------------------- chat window (Messages-like, flat)
  // update({ x, y, show, from: "x"|"y", incoming, sent }) — x/y top-left. stickerRect() is relative to the window.
  function Chat(parent, spec) {
    const root = el("div", { class: "abs chat" }, parent);
    set(root, { width: spec.w + "px", height: spec.h + "px" });
    const bar = el("div", { class: "ch-bar" }, root);
    const lights = el("div", { class: "f-lights ch-lights" }, bar);
    ["#FF5F57", "#FEBC2E", "#28C840"].forEach((c) => el("span", { style: { background: c } }, lights));
    const who = el("div", { class: "ch-who" }, bar);
    el("div", { class: "ch-avatar" }, who, spec.initial);
    el("div", { class: "ch-name" }, who, spec.name);
    const body = el("div", { class: "ch-body" }, root);
    const incoming = el("div", { class: "ch-in" }, body, spec.msg);
    const sticker = el("div", { class: "ch-sticker" }, body);
    set(sticker, { backgroundImage: "url(" + spec.sticker + ")", width: spec.sw + "px", height: spec.sh + "px", right: "26px", top: spec.sy + "px" });
    const sent = el("div", { class: "ch-sent" }, body, spec.sent);
    set(sent, { top: spec.sy + spec.sh + 8 + "px" });
    el("div", { class: "ch-field" }, root, "iMessage");
    const BAR = 52;
    function update(s) {
      const show = s.show || 0;
      const out = s.out || 0;
      const vis = show > 0.001 && out < 0.999;
      const dx = s.from === "y" ? 0 : (1 - show) * 140 + out * 180, dy = s.from === "y" ? (1 - show) * 160 + out * 200 : 0;
      set(root, { display: vis ? "block" : "none", left: s.x + "px", top: s.y + "px", transform: "translate(" + dx + "px," + dy + "px) scale(" + lerp(0.96, 1, show) + ")", opacity: clamp(show * 2.5) * (1 - out) });
      const inc = s.incoming || 0;
      set(incoming, { opacity: clamp(inc * 2), transform: "translateY(" + (1 - inc) * 14 + "px) scale(" + lerp(0.85, 1, inc) + ")" });
      set(sticker, { opacity: s.sticker ? 1 : 0 });
      set(sent, { opacity: s.sent || 0 });
    }
    const stickerRect = () => ({ x: spec.w - 26 - spec.sw, y: BAR + spec.sy, w: spec.sw, h: spec.sh });
    return { update, stickerRect, root };
  }

  PV.UI = { K, ACCENT, INK, G, Cursor, Keycap, Finder, Wheel, Card, Terminal, Phone, Chat, WIN, PHONE };
})();

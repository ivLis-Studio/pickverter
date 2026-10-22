// Live wheel for the hero: the app's geometry (WheelView.swift), hit-testing, outer option rings and keyboard.
// The Windows page shows the Windows app's wording and keys (Shift · Ctrl · Alt) from APP_WIN.
(function () {
  const NS = "http://www.w3.org/2000/svg";
  const G = { outer: 150, inner: 62, disc: 158, subInner: 164, subOuter: 218, box: 480, tabW: 94, tabH: 28, tabSp: 2, tabPad: 3, tabGap: 12 };
  const C = G.box / 2, SUB_SPAN = 27, DEAD = G.inner * 0.62;
  const rad = (d) => (d * Math.PI) / 180;
  const polar = (r, deg) => [C + Math.cos(rad(deg)) * r, C + Math.sin(rad(deg)) * r];

  function ringPath(startDeg, endDeg, inner, outer, gap, corner) {
    const ri = inner + corner, ro = outer - corner, half = gap / 2 + corner;
    const s = rad(startDeg), e = rad(endDeg), dso = half / ro, dsi = half / ri;
    const p = (r, a) => (C + Math.cos(a) * r).toFixed(2) + " " + (C + Math.sin(a) * r).toFixed(2);
    const lo = e - dso - (s + dso) > Math.PI ? 1 : 0, li = e - dsi - (s + dsi) > Math.PI ? 1 : 0;
    return "M" + p(ro, s + dso) + " A" + ro + " " + ro + " 0 " + lo + " 1 " + p(ro, e - dso) + " L" + p(ri, e - dsi) +
      " A" + ri + " " + ri + " 0 " + li + " 0 " + p(ri, s + dsi) + " Z";
  }
  function arcPath(r, a0, a1) {
    return "M" + (C + Math.cos(rad(a0)) * r) + " " + (C + Math.sin(rad(a0)) * r) + " A" + r + " " + r + " 0 0 1 " + (C + Math.cos(rad(a1)) * r) + " " + (C + Math.sin(rad(a1)) * r);
  }
  const el = (tag, attrs = {}, parent, html) => {
    const n = ["div", "span", "b", "small"].includes(tag) ? document.createElement(tag) : document.createElementNS(NS, tag);
    for (const k in attrs) n.setAttribute(k, attrs[k]);
    if (html != null) n.innerHTML = html;
    if (parent) parent.appendChild(n);
    return n;
  };
  const icon = (name) => '<span class="wi"><svg viewBox="0 0 24 24" aria-hidden="true">' + (window.ICONS[name] || "") + "</svg></span>";

  function strings(lang, os) {
    const L = window.APP.languages, i = Math.max(0, L.indexOf(lang));
    const W = os === "win" && window.APP_WIN ? window.APP_WIN.strings : {};
    const raw = (k) => (W[k] ? W[k][i] : window.APP.strings[k] ? window.APP.strings[k][i] : k);
    const t = (k, ...a) => raw(k).replace(/%(\d)\$@/g, (_, n) => a[+n - 1]).replace(/%@/g, () => a.shift());
    const tp = (k, n) => { const f = raw(k).split("|"); return (f.length > 1 && n !== 1 ? f[1] : f[0]).replace(/%1\$@|%@/g, n); };
    return { t, tp };
  }

  function rings(lang, os) {
    const { t } = strings(lang, os);
    const win = os === "win";
    const v = (title, ic) => ({ title, icon: ic });
    return {
      convert: ["JPG", "PNG", "WebP", "AVIF", "TIFF", "GIF", "BMP", "PDF", "ICO"].map((f) => ({ title: f, format: true })),
      tools: [
        { title: t("tool.compress"), icon: "shrink", d: t("tool.compress.d"), variants: [v(t("q.balanced"), "shrink"), v(t("q.high"), "image"), v(t("q.small"), "shrink")] },
        { title: t("tool.resize"), icon: "scaling", d: t("tool.resize.d"), variants: [v("50%", "scaling"), v("25%", "scaling"), v("75%", "scaling")] },
        { title: t("tool.crop"), icon: "crop", d: t("tool.crop.d"), variants: [v(t("crop.custom"), "crop"), v("1:1", "crop"), v("16:9", "crop")] },
        { title: t("tool.removebg"), icon: "scissors", d: t("tool.removebg.d"), variants: [v(t("bg.transparent"), "layers"), v(t("bg.crop"), "crop"), v(t("bg.white"), "image")] },
        { title: t("tool.redact"), icon: "eye-off", d: t("tool.redact.d"), variants: [v(t("redact.custom"), "eye-off"), v(t("redact.auto_faces"), "scan-text"), v(t("redact.auto_text"), "scan-text")] },
        { title: t("tool.ocr"), icon: "scan-text", d: t("tool.ocr.d") },
        { title: t("tool.rotate"), icon: "rotate-cw", d: t("tool.rotate.d"), variants: [v(t("rot.right"), "rotate-cw"), v(t("rot.left"), "rotate-ccw"), v(t("rot.half"), "refresh-ccw"), v(t("rot.flipH"), "flip-vertical-2"), v(t("rot.flipV"), "flip-horizontal-2")] },
        { title: t("tool.meta"), icon: "info", d: t("tool.meta.image.d"), variants: [v(t("meta.edit"), "info"), v(t("strip.all"), "eye-off"), v(t("strip.gps"), "eye-off")] },
        { title: t("tool.zip"), icon: "archive", d: t("tool.zip.d") },
      ],
      actions: [
        win ? { title: t("action.share"), icon: "share", d: t("action.share.d") } : { title: "AirDrop", icon: "radio", d: t("action.airdrop.d") },
        { title: t("action.terminal"), icon: "terminal", d: t("action.terminal.d", win ? "Windows Terminal" : t("action.terminal")) },
        { title: t("action.copypath"), icon: "link", d: t("action.copypath.d"), variants: win
          ? [v(t("path.full"), "link"), v(t("path.quoted"), "link"), v(t("path.name"), "link"), v(t("path.unix"), "link")]
          : [v(t("path.full"), "link"), v(t("path.tilde"), "link"), v(t("path.name"), "link"), v(t("path.quoted"), "link")] },
        { title: t("action.copyfile"), icon: "clipboard", d: t("action.copyfile.d") },
        { title: t("action.move"), icon: "folder-input", d: t("action.move.d"), variants: [v(t("folder.choose"), "folder"), v(t("folder.desktop"), "folder"), v(t("folder.downloads"), "download")] },
        { title: t("action.duplicate"), icon: "copy-plus", d: t("action.duplicate.d") },
        { title: t("action.float"), icon: "pin", d: t("action.float.d") },
        win ? { title: t("action.openwith"), icon: "external-link", d: t("action.openwith.d") } : { title: t("action.share"), icon: "share", d: t("action.share.d") },
        { title: "VS Code", icon: "code", d: t("action.editor.d", "VS Code") },
        { title: t("action.hash"), icon: "hash", d: t("action.hash.d"), variants: [v("SHA-256", "hash"), v("SHA-1", "hash"), v("MD5", "hash")] },
      ],
    };
  }

  function mount(host, opts) {
    const MODES = ["convert", "tools", "actions"];
    const state = { lang: opts.lang, os: opts.os || "mac", mode: "convert", slot: null, sub: null, open: null, flash: null, pointer: null, user: false };
    let R = rings(state.lang, state.os), S = strings(state.lang, state.os);
    host.innerHTML = "";
    const svg = el("svg", { viewBox: "0 0 " + G.box + " " + G.box, class: "wsvg", "aria-hidden": "true" }, host);
    const defs = el("defs", {}, svg);
    const f = el("filter", { id: "wshadow", x: "-30%", y: "-30%", width: "160%", height: "160%" }, defs);
    el("feGaussianBlur", { stdDeviation: 10 }, f);
    el("circle", { cx: C, cy: C + 7, r: G.disc, fill: "#000", opacity: 0.16, filter: "url(#wshadow)" }, svg);
    const subLayer = el("g", {}, svg);
    el("circle", { cx: C, cy: C, r: G.disc, class: "w-disc" }, svg);
    const segLayer = el("g", {}, svg);
    const labelFO = el("foreignObject", { x: 0, y: 0, width: G.box, height: G.box, class: "w-labels" }, svg);
    const labels = el("div", { class: "wlbox" }, labelFO);
    el("circle", { cx: C, cy: C, r: G.inner - 5, class: "w-hub" }, svg);
    const arc = el("path", { class: "w-arc" }, svg);
    const hubFO = el("foreignObject", { x: C - (G.inner - 5), y: C - (G.inner - 5), width: (G.inner - 5) * 2, height: (G.inner - 5) * 2 }, svg);
    const hub = el("div", { class: "whub" }, hubFO);
    // Windows tabs spell the key out ("Shift Convert"), so they are wider.
    let tw = G.tabW, tabW = 0;
    const tabH = G.tabH + G.tabPad * 2, tabY = C + G.disc + G.tabGap;
    const tabsG = el("g", { class: "w-tabs" }, svg);
    const tabBg = el("rect", { y: tabY, height: tabH, rx: tabH / 2, class: "w-tabbg" }, tabsG);
    const pill = el("rect", { y: tabY + G.tabPad, height: G.tabH, rx: G.tabH / 2, class: "w-pill" }, tabsG);
    const tabFO = el("foreignObject", { y: tabY, height: tabH }, tabsG);
    const tabRow = el("div", { class: "wtabs" }, tabFO);
    const card = el("div", { class: "wcard", role: "status" }, host);
    function layoutTabs() {
      tw = state.os === "win" ? 112 : G.tabW;
      tabW = tw * 3 + G.tabSp * 2 + G.tabPad * 2;
      tabBg.setAttribute("x", C - tabW / 2); tabBg.setAttribute("width", tabW);
      pill.setAttribute("width", tw);
      tabFO.setAttribute("x", C - tabW / 2); tabFO.setAttribute("width", tabW);
    }
    const KEYCAPS = () => (state.os === "win" ? [S.t("key.shift"), S.t("key.ctrl"), S.t("key.alt")] : ["⇧", "⌘", "⌃"]);
    const MODKEYS = () => (state.os === "win" ? { Shift: "convert", Control: "tools", Alt: "actions" } : { Shift: "convert", Meta: "tools", Control: "actions" });

    let segs = [], subs = [];
    function build() {
      segLayer.innerHTML = ""; subLayer.innerHTML = ""; labels.innerHTML = "";
      const items = R[state.mode], n = items.length, span = 360 / n;
      segs = items.map((it, i) => {
        const mid = -90 + span * i;
        const p = el("path", { d: ringPath(mid - span / 2, mid + span / 2, G.inner, G.outer, 3, 8), class: "w-seg" }, segLayer);
        if (it.variants) { const [tx, ty] = polar(G.outer - 7, mid); el("rect", { x: tx - 5, y: ty - 1, width: 10, height: 2, rx: 1, class: "w-tick", transform: "rotate(" + (mid + 90) + " " + tx + " " + ty + ")" }, segLayer); }
        const lr = (G.inner + G.outer) / 2 + 2, [lx, ly] = polar(lr, mid), w = Math.max(Math.min((2 * Math.PI * lr) / n - 12, G.outer - G.inner - 8), 40);
        const lab = el("div", { class: "wl" + (it.format ? " fmt" : "") }, labels, it.format ? it.title : icon(it.icon) + "<span>" + it.title + "</span>");
        Object.assign(lab.style, { left: lx - w / 2 + "px", top: ly - 30 + "px", width: w + "px", height: "60px" });
        return { p, lab, mid };
      });
      subs = items.map((it, i) => {
        if (!it.variants) return null;
        const mid = -90 + span * i, m = it.variants.length, first = mid - (SUB_SPAN * m) / 2;
        const g = el("g", { class: "w-sub" }, subLayer);
        el("path", { d: ringPath(first - 1.2, first + SUB_SPAN * m + 1.2, G.subInner - 4, G.subOuter + 4, 0, 10), class: "w-subband" }, g);
        const lab = el("div", { class: "wsub" }, labels);
        const tiles = it.variants.map((vv, j) => {
          const s0 = first + SUB_SPAN * j;
          const p = el("path", { d: ringPath(s0, s0 + SUB_SPAN, G.subInner, G.subOuter, 3, 7), class: "w-seg sub" + (j === 0 ? " def" : "") }, g);
          const [lx, ly] = polar((G.subInner + G.subOuter) / 2, s0 + SUB_SPAN / 2);
          const l = el("div", { class: "wl small" }, lab, icon(vv.icon) + "<span>" + vv.title + "</span>");
          Object.assign(l.style, { left: lx - 36 + "px", top: ly - 30 + "px", width: "72px", height: "60px" });
          return { p, l };
        });
        return { g, lab, tiles, first, last: first + SUB_SPAN * m };
      });
      layoutTabs();
      const caps = KEYCAPS();
      tabRow.innerHTML = MODES.map((m, i) => '<span class="wtab' + (m === state.mode ? " on" : "") + '" data-mode="' + m + '"><i>' + caps[i] + "</i>" + S.t("mode." + m) + "</span>").join("");
      pill.setAttribute("x", C - tabW / 2 + G.tabPad + MODES.indexOf(state.mode) * (tw + G.tabSp));
      render();
    }

    function hubHTML() {
      const items = R[state.mode];
      if (state.flash) return state.flash;
      if (state.slot == null) {
        return '<i class="hthumb"></i><b class="hname">' + opts.file + '</b><small class="hhint">' + S.t("wheel.hint.click") + "</small>";
      }
      const it = items[state.slot];
      if (it.format) return '<b class="hfmt">' + it.title + "</b><small>Lisbon." + it.title.toLowerCase() + '</small><small class="hhint">' + S.t("hint.alt_clipboard") + "</small>";
      const vv = state.sub != null ? it.variants[state.sub] : null;
      return icon(vv ? vv.icon : it.icon) + (vv ? '<b class="hacc">' + vv.title + "</b><small>" + it.title + "</small>" :
        "<b>" + it.title + "</b><small>" + (it.d || "") + "</small>" + (it.variants ? '<small class="hhint">' + S.tp("hint.options", it.variants.length) + "</small>" : ""));
    }

    function render() {
      segs.forEach((s, i) => {
        const on = state.slot === i, dim = on && state.sub != null;
        s.p.classList.toggle("on", on && !dim); s.p.classList.toggle("dim", dim);
        s.lab.classList.toggle("on", on && !dim);
      });
      subs.forEach((s, i) => {
        if (!s) return;
        const open = state.open === i;
        s.g.classList.toggle("open", open); s.lab.classList.toggle("open", open);
        s.tiles.forEach((tl, j) => { const on = open && state.sub === j; tl.p.classList.toggle("on", on); tl.l.classList.toggle("on", on); });
      });
      tabsG.classList.toggle("hidden", state.open != null);
      const html = hubHTML();
      if (hub.__h !== html) { hub.innerHTML = html; hub.__h = html; }
      if (state.slot != null && state.pointer != null) {
        arc.setAttribute("d", arcPath(G.inner - 5 - 3.5, state.pointer - 14.4, state.pointer + 14.4)); arc.classList.add("on");
      } else if (state.slot != null) {
        const a = segs[state.slot].mid; arc.setAttribute("d", arcPath(G.inner - 5 - 3.5, a - 14.4, a + 14.4)); arc.classList.add("on");
      } else arc.classList.remove("on");
      const it = state.slot != null ? R[state.mode][state.slot] : null;
      const vv = it && state.sub != null ? it.variants[state.sub] : null;
      if (opts.live) opts.live.textContent = vv ? it.title + ", " + vv.title : it ? it.title : "";
    }

    function setMode(m) {
      if (m === state.mode) return;
      state.mode = m; state.slot = null; state.sub = null; state.open = null;
      host.classList.add("swap"); setTimeout(() => host.classList.remove("swap"), 160);
      build();
    }

    function hit(px, py) {
      const dx = px - C, dy = py - C, dist = Math.hypot(dx, dy);
      const a12 = ((Math.atan2(dy, dx) * 180) / Math.PI + 90 + 720) % 360, deg = a12 - 90;
      const items = R[state.mode], n = items.length, span = 360 / n;
      if (dist > G.subOuter + 26) return { slot: null, sub: null, open: null, deg };
      if (dist > G.disc + 1 && state.open != null && subs[state.open]) {
        const s = subs[state.open]; let d = deg;
        while (d < s.first - 180) d += 360; while (d > s.first + 180) d -= 360;
        if (d >= s.first && d <= s.last) return { slot: state.open, sub: Math.min(s.tiles.length - 1, Math.floor((d - s.first) / SUB_SPAN)), open: state.open, deg };
        return { slot: state.open, sub: null, open: state.open, deg };
      }
      if (dist <= DEAD) return { slot: null, sub: null, open: null, deg };
      const slot = Math.floor(((a12 + span / 2) % 360) / span) % n;
      return { slot, sub: null, open: items[slot].variants ? slot : null, deg };
    }

    function toLocal(e) {
      const pt = svg.createSVGPoint(); pt.x = e.clientX; pt.y = e.clientY;
      const m = svg.getScreenCTM(); if (!m) return null;
      const q = pt.matrixTransform(m.inverse()); return [q.x, q.y];
    }

    function run() {
      const items = R[state.mode];
      if (state.slot == null) return;
      const it = items[state.slot], vv = state.sub != null ? it.variants[state.sub] : null;
      const label = it.format ? S.t("job.convert_label", it.title) : vv ? it.title + " · " + vv.title : it.title;
      const sub = it.format ? "Lisbon." + it.title.toLowerCase() : opts.file;
      state.flash = icon("check") + '<b class="hacc">' + opts.done + "</b><small>" + (it.format ? it.title : it.title) + "</small>";
      card.innerHTML = '<i class="wthumb"></i><span><b>' + S.t("job.done", label) + "</b><small>" + sub + "</small></span>";
      card.classList.remove("show"); void card.offsetWidth; card.classList.add("show");
      host.classList.add("pick"); setTimeout(() => host.classList.remove("pick"), 200);
      render();
      clearTimeout(run.t1); clearTimeout(run.t2);
      run.t1 = setTimeout(() => { state.flash = null; render(); }, 900);
      run.t2 = setTimeout(() => card.classList.remove("show"), 2600);
      if (opts.onRun) opts.onRun();
    }

    // ---- input
    function userInput() { state.user = true; stopDemo(); clearTimeout(userInput.t); userInput.t = setTimeout(() => { state.user = false; if (!state.pointerInside) startDemo(); }, 6000); }
    svg.addEventListener("pointermove", (e) => {
      const q = toLocal(e); if (!q) return; userInput(); state.pointerInside = true;
      const h = hit(q[0], q[1]);
      state.slot = h.slot; state.sub = h.sub; state.open = h.open; state.pointer = h.slot != null ? h.deg : null; render();
    });
    svg.addEventListener("pointerleave", () => { state.pointerInside = false; if (state.user) { state.slot = null; state.sub = null; state.open = null; render(); } });
    svg.addEventListener("click", (e) => {
      const q = toLocal(e); if (!q) return; userInput();
      const ty = C + G.disc + G.tabGap;
      if (state.open == null && q[1] >= ty && q[1] <= ty + tabH && Math.abs(q[0] - C) <= tabW / 2) {
        setMode(MODES[Math.min(2, Math.max(0, Math.floor((q[0] - (C - tabW / 2)) / (tabW / 3))))]); return;
      }
      const h = hit(q[0], q[1]);
      state.slot = h.slot; state.sub = h.sub; state.open = h.open; run();
    });
    let modTap = null;
    host.addEventListener("keydown", (e) => {
      const items = R[state.mode], n = items.length;
      if (e.key in MODKEYS()) { modTap = e.key; if (e.key === "Alt") e.preventDefault(); return; }
      modTap = null;
      let handled = true;
      userInput();
      if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
        const d = e.key === "ArrowRight" ? 1 : -1;
        state.slot = state.slot == null ? (d > 0 ? 0 : n - 1) : (state.slot + d + n) % n;
        state.sub = null; state.open = items[state.slot].variants ? state.slot : null; state.pointer = null;
      } else if ((e.key === "ArrowDown" || e.key === "ArrowUp") && state.slot != null && items[state.slot].variants) {
        const m = items[state.slot].variants.length, d = e.key === "ArrowDown" ? 1 : -1;
        state.open = state.slot; state.sub = state.sub == null ? (d > 0 ? 0 : m - 1) : (state.sub + d + m) % m;
      } else if (e.key === "Enter" || e.key === " ") { run(); }
      else if (/^[1-9]$/.test(e.key) && +e.key <= n) { state.slot = +e.key - 1; state.sub = null; state.open = null; run(); }
      else if (e.key === "Escape") { state.slot = null; state.sub = null; state.open = null; }
      else handled = false;
      if (handled) { e.preventDefault(); render(); }
    });
    host.addEventListener("keyup", (e) => {
      if (modTap && e.key === modTap) { setMode(MODKEYS()[e.key]); userInput(); if (e.key === "Alt") e.preventDefault(); }
      modTap = null;
    });

    // ---- idle demo (the same beats as the film)
    const reduce = matchMedia("(prefers-reduced-motion: reduce)").matches;
    const script = [
      ["convert", 0, null], ["convert", 1, null], ["convert", 1, null, "run"], ["convert", null, null],
      ["tools", null, null], ["tools", 3, null], ["tools", 6, null], ["tools", 6, 0], ["tools", 6, 1], ["tools", 6, 1, "run"], ["tools", null, null],
      ["actions", null, null], ["actions", 0, null], ["actions", 1, null], ["actions", 1, null, "run"], ["actions", null, null], ["convert", null, null],
    ];
    let step = 0, timer = null;
    function tick() {
      const [m, slot, sub, act] = script[step % script.length];
      if (m !== state.mode) setMode(m);
      state.slot = slot; state.sub = sub; state.open = slot != null && R[m][slot].variants ? slot : null; state.pointer = null;
      if (act === "run") run(); else render();
      step++;
    }
    function startDemo() { if (reduce || timer || !visible) return; timer = setInterval(tick, 950); }
    function stopDemo() { clearInterval(timer); timer = null; }
    let visible = false;
    new IntersectionObserver((es) => { visible = es[0].isIntersecting; if (visible && !state.user) startDemo(); else stopDemo(); }, { threshold: 0.3 }).observe(host);

    build();
    return {
      setLang(lang, os) {
        state.lang = lang; if (os) state.os = os;
        R = rings(lang, state.os); S = strings(lang, state.os); state.slot = null; state.sub = null; state.open = null; build();
      },
      setLabels(o) { Object.assign(opts, o); render(); },
    };
  }

  window.PickWheel = { mount };
})();


// The Pickverter mark. One component morphs between the app's light wheel (m = 0) and the app icon (m = 1),
// so the finale can turn the wheel into the icon without a cut. Units: 1024 canvas (macOS icon grid).
(function () {
  const { el, attr, set, lerp, mix, ringCore, arcPath, polar, clamp } = PV;
  const TILE = "M924.00 512.00 L924.00 640.59 C924.00 722.19 924.00 763.00 910.11 806.92 C892.66 854.88 854.88 892.66 806.92 910.11 C763.00 924.00 722.19 924.00 640.59 924.00 L383.41 924.00 C301.81 924.00 261.00 924.00 217.08 910.11 C169.12 892.66 131.34 854.88 113.89 806.92 C100.00 763.00 100.00 722.19 100.00 640.59 L100.00 383.41 C100.00 301.81 100.00 261.00 113.89 217.08 C131.34 169.12 169.12 131.34 217.08 113.89 C261.00 100.00 301.81 100.00 383.41 100.00 L640.59 100.00 C722.19 100.00 763.00 100.00 806.92 113.89 C854.88 131.34 892.66 169.12 910.11 217.08 C924.00 261.00 924.00 301.81 924.00 383.41 Z";
  // Classic macOS arrow, tip at (0,0), normalised to a height of 1.
  const ARROW = [[0, 0], [0, 0.852], [0.198, 0.67], [0.341, 1.0], [0.473, 0.945], [0.33, 0.621], [0.6, 0.621]];

  const LIGHT = { seg: "#E6E6E9", disc: "#F3F3F5", hub: "#FBFBFC", hair: "#D9D9DE", accent: "#0A7CFF" };
  const DARK = { tile: "#16171A", seg: "#2C2E34", hub: "#F4F4F6", accent: "#0A84FF", ink: "#16171A" };
  const N = 8, PV_PICK = 1; // eight slots, the upper-right one is picked

  function geometry(m) {
    const k = 312 / 150; // the app wheel scaled so its outer edge matches the icon ring
    return {
      inner: lerp(62 * k, 150, m), outer: 312, gap: lerp(3.2 * k, 22, m), corner: lerp(8 * k, 21, m),
      disc: 158 * k, hub: lerp(57 * k, 102, m),
    };
  }

  function arrowPath(x, y, h) {
    return ARROW.map(([px, py], i) => (i ? "L" : "M") + (x + px * h) + " " + (y + py * h)).join(" ") + " Z";
  }

  function docPath(cx, cy, w, h) {
    const x = cx - w / 2, y = cy - h / 2, f = w * 0.34, r = w * 0.12;
    return "M" + (x + r) + " " + y + " L" + (x + w - f) + " " + y + " L" + (x + w) + " " + (y + f) +
      " L" + (x + w) + " " + (y + h - r) + " Q" + (x + w) + " " + (y + h) + " " + (x + w - r) + " " + (y + h) +
      " L" + (x + r) + " " + (y + h) + " Q" + x + " " + (y + h) + " " + x + " " + (y + h - r) +
      " L" + x + " " + (y + r) + " Q" + x + " " + y + " " + (x + r) + " " + y + " Z";
  }

  function Icon(parent, size, id = "ic", opts = {}) {
    const PICK = opts.pick ?? PV_PICK;
    const cursorH = opts.cursorH ?? 236;
    const cursorOn = opts.cursor !== false;
    const svg = el("svg", { viewBox: "0 0 1024 1024", width: size, height: size, class: "mark", style: { overflow: "visible" } }, parent);
    const defs = el("defs", {}, svg);
    const tileShadow = el("filter", { id: id + "-ts", x: "-20%", y: "-20%", width: "140%", height: "140%" }, defs);
    el("feDropShadow", { dx: 0, dy: 12, stdDeviation: 14, "flood-color": "#000", "flood-opacity": 0.28 }, tileShadow);
    const pickShadow = el("filter", { id: id + "-ps", x: "-40%", y: "-40%", width: "180%", height: "180%" }, defs);
    const pickDrop = el("feDropShadow", { dx: 0, dy: 10, stdDeviation: 14, "flood-color": "#000", "flood-opacity": 0.4 }, pickShadow);
    const discShadow = el("filter", { id: id + "-ds", x: "-30%", y: "-30%", width: "160%", height: "160%" }, defs);
    el("feDropShadow", { dx: 0, dy: 14, stdDeviation: 26, "flood-color": "#000", "flood-opacity": 0.13 }, discShadow);
    const cursorShadow = el("filter", { id: id + "-cs", x: "-50%", y: "-50%", width: "200%", height: "200%" }, defs);
    el("feDropShadow", { dx: 0, dy: 8, stdDeviation: 10, "flood-color": "#000", "flood-opacity": 0.35 }, cursorShadow);

    const tileG = el("g", {}, svg);
    const tile = el("path", { d: TILE, filter: "url(#" + id + "-ts)" }, tileG);
    const tileEdge = el("path", { d: TILE, fill: "none", stroke: "#fff", "stroke-opacity": 0.09, "stroke-width": 4 }, tileG);
    const disc = el("circle", { cx: 512, cy: 512, filter: "url(#" + id + "-ds)" }, svg);
    const discEdge = el("circle", { cx: 512, cy: 512, fill: "none", "stroke-width": 2 }, svg);
    const segs = [];
    for (let i = 0; i < N; i++) {
      const g = el("g", {}, svg);
      const p = el("path", { "stroke-linejoin": "round" }, g);
      segs.push({ g, p });
    }
    const hub = el("circle", { cx: 512, cy: 512 }, svg);
    const hubEdge = el("circle", { cx: 512, cy: 512, fill: "none", "stroke-width": 2 }, svg);
    const arc = el("path", { fill: "none", "stroke-linecap": "round" }, svg);
    const doc = el("path", {}, svg);
    const fold = el("path", { fill: "none", "stroke-linejoin": "round", "stroke-linecap": "round" }, svg);
    const cursorG = el("g", { filter: "url(#" + id + "-cs)" }, svg);
    const cursor = el("path", { "stroke-linejoin": "round" }, cursorG);

    // p: m (light wheel → icon), tile (0..1 grow), lit (0..1 picked slot colour), pop (0..1 picked offset),
    //    hl (index lit in the light wheel, or -1), hlMix, cursor (0..1 fly-in), press (0..1), doc (0..1), spin (deg)
    function update(p) {
      const m = clamp(p.m ?? 1);
      const g = geometry(m);
      const tileAmt = clamp(p.tile ?? m);
      const ts = lerp(0.35, 1, tileAmt);
      attr(tileG, "transform", "translate(512 512) scale(" + ts + ") translate(-512 -512)");
      attr(tileG, "opacity", clamp(tileAmt * 3));
      attr(tile, "fill", DARK.tile);
      attr(disc, "r", g.disc);
      attr(disc, "fill", LIGHT.disc);
      attr(disc, "opacity", clamp(1 - m * 1.6));
      attr(discEdge, "r", g.disc - 1);
      attr(discEdge, "stroke", LIGHT.hair);
      attr(discEdge, "opacity", clamp(1 - m * 1.6));
      const span = 360 / N;
      const spin = p.spin || 0;
      for (let i = 0; i < N; i++) {
        const mid = -90 + span * i + spin;
        const picked = i === PICK;
        const pop = picked ? (p.pop ?? m) : 0;
        const [dx, dy] = polar(0, 0, 24 * pop, mid);
        const base = mix(LIGHT.seg, DARK.seg, m);
        let color = base;
        if (p.hl === i) color = mix(base, LIGHT.accent, p.hlMix ?? 1);
        if (picked) color = mix(color, mix(LIGHT.accent, DARK.accent, m), clamp(p.lit ?? m));
        const d = ringCore(512, 512, mid - span / 2, mid + span / 2, g.inner, g.outer, g.gap, g.corner);
        attr(segs[i].p, "d", d);
        attr(segs[i].p, "fill", color);
        attr(segs[i].p, "stroke", color);
        attr(segs[i].p, "stroke-width", g.corner * 2);
        attr(segs[i].g, "transform", "translate(" + dx + " " + dy + ")");
        attr(segs[i].g, "filter", picked && pop > 0.02 ? "url(#" + id + "-ps)" : "none");
      }
      attr(pickDrop, "flood-opacity", 0.42 * clamp(p.pop ?? m));
      attr(hub, "r", g.hub);
      attr(hub, "fill", mix(LIGHT.hub, DARK.hub, m));
      attr(hubEdge, "r", g.hub - 1);
      attr(hubEdge, "stroke", LIGHT.hair);
      attr(hubEdge, "opacity", clamp(1 - m * 1.5));
      const arcAmt = clamp(p.arc ?? 0);
      const arcMid = -90 + span * PICK + spin;
      attr(arc, "d", arcPath(512, 512, g.hub - 16, arcMid - 15, arcMid + 15));
      attr(arc, "stroke", DARK.accent);
      attr(arc, "stroke-width", 11);
      attr(arc, "opacity", arcAmt);
      const docAmt = clamp(p.doc ?? m);
      const dw = 98 * lerp(0.6, 1, docAmt), dh = 126 * lerp(0.6, 1, docAmt);
      attr(doc, "d", docPath(512, 512, dw, dh));
      attr(doc, "fill", DARK.ink);
      attr(doc, "opacity", docAmt);
      const fx = 512 + dw / 2 - dw * 0.34, fy = 512 - dh / 2;
      attr(fold, "d", "M" + fx + " " + (fy - 4) + " L" + fx + " " + (fy + dw * 0.34) + " L" + (512 + dw / 2 + 4) + " " + (fy + dw * 0.34));
      attr(fold, "stroke", mix(LIGHT.hub, DARK.hub, m));
      attr(fold, "stroke-width", dw * 0.07);
      attr(fold, "opacity", docAmt);
      // Cursor: tip lands on the picked slot.
      const c = clamp(p.cursor ?? m);
      const [tx, ty] = polar(512, 512, (g.inner + g.outer) / 2 + 26, -90 + span * PICK + spin);
      const h = cursorH * (1 - 0.06 * (p.press || 0));
      const fx2 = tx + (1 - c) * 260, fy2 = ty + (1 - c) * 300;
      attr(cursor, "d", arrowPath(fx2 - 4, fy2 - 4, h));
      attr(cursor, "fill", "#ffffff");
      attr(cursor, "stroke", DARK.ink);
      attr(cursor, "stroke-width", 15);
      attr(cursorG, "opacity", cursorOn ? clamp(c * 4) : 0);
    }
    return { svg, update, geometry };
  }

  PV.Icon = Icon;
  PV.ICON = { LIGHT, DARK, N, PICK: PV_PICK, geometry };
})();


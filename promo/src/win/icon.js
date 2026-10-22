// The Windows app icon (windows/scripts/make-icons.mjs, BrandArt.cs): a graphite plate with Windows' rounded corners,
// eight slots with the upper-right one picked and lifted, a document in the hub and the Windows arrow that picked it.
// Units: the 1024 icon grid. update({ pop, cursor, press, shadow }) animates the finale.
(function () {
  const { el, attr, polar, ringCore, clamp } = PV;
  const PLATE = "#16171A", SLOT = "#2C2E34", PICK = "#0A84FF", HUB = "#F4F4F6";
  const ARROW = [[0, 0], [0, 0.842], [0.21, 0.632], [0.368, 1.0], [0.474, 0.947], [0.316, 0.579], [0.579, 0.579]];
  const N = 8, PICKED = 1, INNER = 150, OUTER = 312, GAP = 26, CORNER = 21, LIFT = 24;
  const SIDE = 864, INSET = (1024 - SIDE) / 2, RADIUS = SIDE * 0.174;

  const arrowPath = (x, y, h) => ARROW.map(([px, py], i) => (i ? "L" : "M") + (x + px * h).toFixed(2) + " " + (y + py * h).toFixed(2)).join(" ") + " Z";
  function docPath(cx, cy, w, h) {
    const x = cx - w / 2, y = cy - h / 2, f = w * 0.34, r = w * 0.12;
    return "M" + (x + r) + " " + y + " L" + (x + w - f) + " " + y + " L" + (x + w) + " " + (y + f) + " L" + (x + w) + " " + (y + h - r) +
      " Q" + (x + w) + " " + (y + h) + " " + (x + w - r) + " " + (y + h) + " L" + (x + r) + " " + (y + h) + " Q" + x + " " + (y + h) + " " + x + " " + (y + h - r) +
      " L" + x + " " + (y + r) + " Q" + x + " " + y + " " + (x + r) + " " + y + " Z";
  }

  function Icon(parent, size, id) {
    id = id || "wi";
    const svg = el("svg", { viewBox: "0 0 1024 1024", width: size, height: size, class: "mark", style: { overflow: "visible" } }, parent);
    const defs = el("defs", {}, svg);
    const ts = el("filter", { id: id + "-ts", x: "-20%", y: "-20%", width: "140%", height: "140%" }, defs);
    const tileDrop = el("feDropShadow", { dx: 0, dy: 12, stdDeviation: 14, "flood-color": "#000", "flood-opacity": 0.28 }, ts);
    const ps = el("filter", { id: id + "-ps", x: "-40%", y: "-40%", width: "180%", height: "180%" }, defs);
    const pickDrop = el("feDropShadow", { dx: 0, dy: 10, stdDeviation: 14, "flood-color": "#000", "flood-opacity": 0.4 }, ps);
    const cs = el("filter", { id: id + "-cs", x: "-50%", y: "-50%", width: "200%", height: "200%" }, defs);
    el("feDropShadow", { dx: 0, dy: 8, stdDeviation: 10, "flood-color": "#000", "flood-opacity": 0.35 }, cs);

    const plate = el("rect", { x: INSET, y: INSET, width: SIDE, height: SIDE, rx: RADIUS, fill: PLATE, filter: "url(#" + id + "-ts)" }, svg);
    el("rect", { x: INSET + 2, y: INSET + 2, width: SIDE - 4, height: SIDE - 4, rx: RADIUS - 2, fill: "none", stroke: "#fff", "stroke-opacity": 0.09, "stroke-width": 4 }, svg);
    const span = 360 / N;
    const segs = [];
    for (let i = 0; i < N; i++) {
      const mid = -90 + i * span;
      const color = i === PICKED ? PICK : SLOT;
      const g = el("g", {}, svg);
      el("path", { d: ringCore(512, 512, mid - span / 2, mid + span / 2, INNER, OUTER, GAP, CORNER), fill: color, stroke: color, "stroke-width": CORNER * 2, "stroke-linejoin": "round" }, g);
      segs.push({ g, mid });
    }
    el("circle", { cx: 512, cy: 512, r: 102, fill: HUB }, svg);
    const w = 98, h = 126, fx = 512 + w / 2, fy = 512 - h / 2, fo = w * 0.34;
    el("path", { d: docPath(512, 512, w, h), fill: PLATE }, svg);
    el("path", { d: "M" + (fx - fo) + " " + fy + " L" + (fx - fo) + " " + (fy + fo) + " L" + fx + " " + (fy + fo), fill: "none", stroke: HUB, "stroke-width": w * 0.07, "stroke-linejoin": "round" }, svg);
    const cursorG = el("g", { filter: "url(#" + id + "-cs)" }, svg);
    const cursor = el("path", { fill: "#fff", stroke: PLATE, "stroke-width": 15, "stroke-linejoin": "round" }, cursorG);
    const [tx, ty] = polar(512, 512, (INNER + OUTER) / 2 + 26, -90 + span * PICKED);

    // p: pop (0..1 lift of the picked slot), cursor (0..1 fly-in), press (0..1), shadow (0..1 plate shadow)
    function update(p) {
      const shadow = clamp(p.shadow ?? 1);
      attr(tileDrop, "flood-opacity", 0.28 * shadow);
      attr(plate, "filter", shadow > 0.01 ? "url(#" + id + "-ts)" : "none");
      const pop = p.pop ?? 1;
      const s = segs[PICKED];
      const [dx, dy] = polar(0, 0, LIFT * pop, s.mid);
      attr(s.g, "transform", "translate(" + dx + " " + dy + ")");
      attr(s.g, "filter", pop > 0.02 ? "url(#" + id + "-ps)" : "none");
      attr(pickDrop, "flood-opacity", 0.4 * clamp(pop));
      const c = clamp(p.cursor ?? 1);
      const hh = 190 * (1 - 0.06 * (p.press || 0));
      attr(cursor, "d", arrowPath(tx - 4 + (1 - c) * 260, ty - 4 + (1 - c) * 300, hh));
      attr(cursorG, "opacity", clamp(c * 4));
    }
    return { svg, update };
  }

  PV.WinIcon = Icon;
  PV.WINICON = { PICK, PLATE, N, PICKED, INNER, OUTER };
})();


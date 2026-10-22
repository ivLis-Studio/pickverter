// Shared helpers for the deterministic promo stage: every frame is a pure function of time.
(function () {
  const clamp = (x, a = 0, b = 1) => Math.min(b, Math.max(a, x));
  const lerp = (a, b, t) => a + (b - a) * t;
  const prog = (t, a, b) => clamp((t - a) / (b - a));
  const E = {
    linear: (x) => x,
    inQuad: (x) => x * x,
    outQuad: (x) => 1 - (1 - x) * (1 - x),
    inCubic: (x) => x * x * x,
    outCubic: (x) => 1 - Math.pow(1 - x, 3),
    inOutCubic: (x) => (x < 0.5 ? 4 * x * x * x : 1 - Math.pow(-2 * x + 2, 3) / 2),
    outQuart: (x) => 1 - Math.pow(1 - x, 4),
    inOutQuart: (x) => (x < 0.5 ? 8 * x * x * x * x : 1 - Math.pow(-2 * x + 2, 4) / 2),
    outQuint: (x) => 1 - Math.pow(1 - x, 5),
    inExpo: (x) => (x === 0 ? 0 : Math.pow(2, 10 * x - 10)),
    outExpo: (x) => (x === 1 ? 1 : 1 - Math.pow(2, -10 * x)),
    inOutExpo: (x) => (x === 0 ? 0 : x === 1 ? 1 : x < 0.5 ? Math.pow(2, 20 * x - 10) / 2 : (2 - Math.pow(2, -20 * x + 10)) / 2),
    outBack: (x) => { const c1 = 1.4, c3 = c1 + 1; return 1 + c3 * Math.pow(x - 1, 3) + c1 * Math.pow(x - 1, 2); },
    inOutSine: (x) => -(Math.cos(Math.PI * x) - 1) / 2,
  };

  // SwiftUI-style spring(response, dampingFraction) from 0 to 1, t in seconds since start.
  function spring(t, response = 0.35, damping = 0.8) {
    if (t <= 0) return 0;
    const w0 = (2 * Math.PI) / response;
    const z = damping;
    if (z < 1) {
      const wd = w0 * Math.sqrt(1 - z * z);
      return 1 - Math.exp(-z * w0 * t) * (Math.cos(wd * t) + ((z * w0) / wd) * Math.sin(wd * t));
    }
    return 1 - Math.exp(-w0 * t) * (1 + w0 * t);
  }

  // Piecewise keyframes: [[t, value, ease?], ...]; ease applies to the segment ending at that key.
  function kf(t, keys) {
    if (t <= keys[0][0]) return keys[0][1];
    for (let i = 1; i < keys.length; i++) {
      const [t1, v1, ease] = keys[i];
      const [t0, v0] = keys[i - 1];
      if (t <= t1) {
        const p = t1 === t0 ? 1 : (t - t0) / (t1 - t0);
        const e = (ease || E.inOutCubic)(p);
        if (Array.isArray(v0)) return v0.map((a, j) => lerp(a, v1[j], e));
        return lerp(v0, v1, e);
      }
    }
    return keys[keys.length - 1][1];
  }

  // 0 → 1 on enter, 1 → 0 on leave, each over its own duration and easing.
  function window_(t, start, end, inDur = 0.3, outDur = 0.3, inEase = E.outCubic, outEase = E.inCubic) {
    if (t < start || t > end + outDur) return 0;
    const a = inEase(prog(t, start, start + inDur));
    const b = end === Infinity ? 1 : 1 - outEase(prog(t, end, end + outDur));
    return Math.min(a, b);
  }

  // A key/hover history [{t, i}] → how strongly item i is lit at time t (0..1), with fades.
  function lit(events, i, t, fade = 0.12) {
    let amount = 0;
    for (let k = 0; k < events.length; k++) {
      const ev = events[k];
      if (ev.i !== i) continue;
      const start = ev.t;
      const end = k + 1 < events.length ? events[k + 1].t : Infinity;
      if (t < start) continue;
      const on = E.outQuad(prog(t, start, start + fade));
      const off = t > end ? 1 - E.outQuad(prog(t, end, end + fade)) : 1;
      amount = Math.max(amount, Math.min(on, off));
    }
    return amount;
  }

  function current(events, t) {
    let value = null;
    for (const ev of events) if (t >= ev.t) value = ev.i;
    return value;
  }

  const rad = (d) => (d * Math.PI) / 180;
  const polar = (cx, cy, r, deg) => [cx + Math.cos(rad(deg)) * r, cy + Math.sin(rad(deg)) * r];

  // Same construction as RingSegment in WheelView.swift: an annular core, then grown by "corner"
  // with round joins. In SVG: fill + stroke of width 2*corner in the same color.
  function ringCore(cx, cy, startDeg, endDeg, inner, outer, gap = 3, corner = 8) {
    const ri = inner + corner, ro = outer - corner;
    const half = gap / 2 + corner;
    const s = rad(startDeg), e = rad(endDeg);
    const dso = half / ro, dsi = half / ri;
    if (ro <= ri || e - s <= 2 * dsi + 0.02) return "";
    const p = (r, a) => [cx + Math.cos(a) * r, cy + Math.sin(a) * r];
    const [x0, y0] = p(ro, s + dso), [x1, y1] = p(ro, e - dso);
    const [x2, y2] = p(ri, e - dsi), [x3, y3] = p(ri, s + dsi);
    const largeO = e - dso - (s + dso) > Math.PI ? 1 : 0;
    const largeI = e - dsi - (s + dsi) > Math.PI ? 1 : 0;
    return "M" + x0 + " " + y0 + " A" + ro + " " + ro + " 0 " + largeO + " 1 " + x1 + " " + y1 +
      " L" + x2 + " " + y2 + " A" + ri + " " + ri + " 0 " + largeI + " 0 " + x3 + " " + y3 + " Z";
  }

  function arcPath(cx, cy, r, startDeg, endDeg) {
    const [x0, y0] = polar(cx, cy, r, startDeg), [x1, y1] = polar(cx, cy, r, endDeg);
    const large = endDeg - startDeg > 180 ? 1 : 0;
    return "M" + x0 + " " + y0 + " A" + r + " " + r + " 0 " + large + " 1 " + x1 + " " + y1;
  }

  const NS = "http://www.w3.org/2000/svg";
  function el(tag, attrs = {}, parent, html) {
    const svg = ["svg", "path", "g", "circle", "rect", "defs", "filter", "feDropShadow", "feGaussianBlur", "clipPath", "mask", "text", "line", "use"].includes(tag);
    const node = svg ? document.createElementNS(NS, tag) : document.createElement(tag);
    for (const [k, v] of Object.entries(attrs)) {
      if (k === "style" && typeof v === "object") Object.assign(node.style, v);
      else if (k === "class") node.setAttribute("class", v);
      else node.setAttribute(k, v);
    }
    if (html !== undefined) node.innerHTML = html;
    if (parent) parent.appendChild(node);
    return node;
  }

  // An SF Symbol rendered as a tintable mask (exported by tools/export-assets.swift).
  function sym(name, size, color, parent, extra = {}) {
    const node = el("div", { class: "sym" }, parent);
    Object.assign(node.style, {
      width: size + "px", height: size + "px", background: color,
      webkitMaskImage: "url(assets/symbols/" + name + ".png)", maskImage: "url(assets/symbols/" + name + ".png)",
    }, extra);
    return node;
  }

  function set(node, styles) {
    for (const k in styles) {
      const v = styles[k];
      if (node.__s && node.__s[k] === v) continue;
      (node.__s || (node.__s = {}))[k] = v;
      node.style[k] = v;
    }
  }

  function attr(node, name, value) {
    const key = "__a_" + name;
    if (node[key] === value) return;
    node[key] = value;
    node.setAttribute(name, value);
  }

  const mix = (a, b, t) => {
    const pa = hex(a), pb = hex(b);
    return "rgb(" + pa.map((c, i) => Math.round(lerp(c, pb[i], t))).join(",") + ")";
  };
  function hex(c) {
    if (c.startsWith("rgb")) return c.slice(c.indexOf("(") + 1, -1).split(",").map(Number);
    const n = parseInt(c.slice(1), 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }

  globalThis.PV = { clamp, lerp, prog, E, spring, kf, win: window_, lit, current, rad, polar, ringCore, arcPath, el, sym, set, attr, mix, hex };
})();


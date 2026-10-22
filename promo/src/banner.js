// README hero: the logo lockup on the left, the real wheel on the right. ?theme=light|dark&lang=en|ko
(function () {
  const { el, polar } = PV;
  const params = new URLSearchParams(location.search);
  const LANG = window.LANG || params.get("lang") || "en";
  const DARK = params.get("theme") === "dark";
  const OG = params.get("og") === "1";
  const I18N = window.I18N || { languages: ["en"], strings: {} };
  const li = Math.max(0, I18N.languages.indexOf(LANG));
  const raw = (k) => (I18N.strings[k] ? I18N.strings[k][li] : k);
  PV.t = (k, ...a) => raw(k).replace(/%(\d)\$@/g, (_, n) => a[+n - 1]).replace(/%@/g, () => a.shift());
  PV.tp = (k, n) => { const f = raw(k).split("|"); return (f.length === 2 ? f[n === 1 ? 0 : 1] : f[0]).replace(/%1\$@|%@/g, String(n)); };
  const COPY = {
    en: { tag: "Pick a file, tap one key,<br>and finish right at your cursor.", chips: ["Free & open source", "macOS 14+", "Apple silicon & Intel", "10 languages"] },
    ko: { tag: "파일을 고르고 키 하나만 누르면,<br>커서 바로 옆에서 끝나요.", chips: ["무료 오픈 소스", "macOS 14 이상", "Apple 실리콘 & Intel", "10개 언어"] },
  }[LANG] || null;
  document.documentElement.lang = LANG;
  if (DARK) document.body.classList.add("dark");
  if (OG) document.documentElement.classList.add("og");

  const stage = document.getElementById("stage");
  const card = el("div", { class: "banner" }, stage);
  const lock = el("div", { class: "lockup" }, card);
  const iconBox = el("div", { class: "b-icon" }, lock);
  PV.Icon(iconBox, 204, "banner").update({ m: 1 });
  const words = el("div", {}, lock);
  el("div", { class: "b-name" }, words, "Pickverter");
  el("div", { class: "b-by" }, words, "by <b>ivLis STUDIO</b>");
  el("div", { class: "b-tag" }, card, COPY.tag);
  const meta = el("div", { class: "b-meta" }, card);
  COPY.chips.forEach((c) => el("div", { class: "b-chip" }, meta, c));

  const K = PV.UI.K;
  const at = OG ? [930, 318] : [1236, 282];
  const pointer = polar(at[0], at[1], 106 * K, -50);
  const rings = { convertHeic: ["JPG", "PNG", "WebP", "AVIF", "TIFF", "GIF", "BMP", "PDF", "ICO"].map((title) => ({ title, format: true })) };
  const wheel = PV.UI.Wheel(card, rings, { id: "banner", at, open: 0, close: 3, modes: [{ t: 0, ring: "convertHeic" }], dark: DARK,
    cursor: () => pointer, file: { name: "Lisbon.heic", thumb: "assets/lisbon.jpg" }, preview: (it) => "Lisbon." + it.title.toLowerCase() });
  const cursor = PV.UI.Cursor(card);

  const faces = ["500 20px 'PV SF'", "600 20px 'PV SF'", "700 20px 'PV SF'"].map((f) => document.fonts.load(f));
  window.ready = Promise.all([...faces, document.fonts.ready]).then(() => {
    wheel.update(2);
    cursor.update({ x: pointer[0], y: pointer[1] });
    return true;
  });
})();


// Page behaviour: language, icons, reveal, tabs, comparison slider, destinations, film, release info.
(function () {
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const LANGS = ["en", "ko"];

  // ---- icons
  $$("[data-i]").forEach((n) => { n.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true">' + (window.ICONS[n.dataset.i] || "") + "</svg>"; });

  // ---- language: ?lang= → saved → browser
  const params = new URLSearchParams(location.search);
  const saved = (() => { try { return localStorage.getItem("pv.lang"); } catch { return null; } })();
  const browser = (navigator.languages || [navigator.language || "en"]).map((l) => l.slice(0, 2).toLowerCase()).find((l) => LANGS.includes(l));
  let lang = [params.get("lang"), saved, browser, "en"].find((l) => LANGS.includes(l));
  const originals = new Map($$("[data-t]").map((n) => [n, n.innerHTML]));
  const X = (k, ...a) => ((window.SITE_EXTRA[lang] || window.SITE_EXTRA.en)[k] || window.SITE_EXTRA.en[k] || k).replace(/%@/g, () => a.shift());

  function applyLang(l) {
    lang = l;
    document.documentElement.lang = l;
    const dict = window.SITE_I18N[l] || {};
    for (const [n, en] of originals) n.innerHTML = l === "en" ? en : dict[n.dataset.t] ?? en;
    $$("[data-t] [data-i], [data-i]").forEach((n) => { if (!n.firstChild) n.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true">' + (window.ICONS[n.dataset.i] || "") + "</svg>"; });
    const meta = window.SITE_META[l] || window.SITE_META.en;
    document.title = meta.title;
    $('meta[name="description"]').setAttribute("content", meta.desc);
    $("#lang-select").value = l;
    $("#play").setAttribute("aria-label", X("film.play"));
    $("#wheel").setAttribute("aria-label", X("wheel.label"));
    setFilm();
    if (wheel) wheel.setLang(l), wheel.setLabels({ done: X("demo.done") });
    renderRelease();
    renderDest();
    renderLangShow();
  }
  $("#lang-select").addEventListener("change", (e) => {
    try { localStorage.setItem("pv.lang", e.target.value); } catch {}
    const u = new URL(location.href); u.searchParams.set("lang", e.target.value); history.replaceState(null, "", u);
    applyLang(e.target.value);
  });

  // ---- header border on scroll
  const bar = $("#bar");
  addEventListener("scroll", () => bar.classList.toggle("scrolled", scrollY > 8), { passive: true });

  // ---- reveal on scroll
  const io = new IntersectionObserver((es) => es.forEach((e) => { if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); } }), { rootMargin: "0px 0px -8% 0px" });
  $$(".reveal").forEach((n) => io.observe(n));

  // ---- live wheel
  let wheel = null;
  wheel = window.PickWheel.mount($("#wheel"), { lang, file: "Lisbon.heic", done: X("demo.done"), live: $("#wheel-live") });

  // ---- ring tabs (roving tabindex)
  const tabs = $$(".ring-tab");
  function select(tab) {
    tabs.forEach((t) => { const on = t === tab; t.setAttribute("aria-selected", on); t.tabIndex = on ? 0 : -1; $("#" + t.getAttribute("aria-controls")).hidden = !on; });
  }
  tabs.forEach((t, i) => {
    t.addEventListener("click", () => select(t));
    t.addEventListener("keydown", (e) => {
      const d = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 }[e.key];
      if (!d) return;
      e.preventDefault(); const n = tabs[(i + d + tabs.length) % tabs.length]; n.focus(); select(n);
    });
  });

  // ---- before / after
  const ba = $("#ba"), range = $("#ba-range");
  range.addEventListener("input", () => ba.style.setProperty("--x", range.value + "%"));

  // ---- destinations
  let dest = "file";
  function renderDest() {
    const titles = { file: X("dest.done.file"), clipboard: X("dest.done.clip"), both: X("dest.done.both"), ask: X("dest.done.ask") };
    $("#dest-sub-out").textContent = titles[dest];
    const app = window.APP, i = Math.max(0, app.languages.indexOf(lang));
    $("#dest-title-out").textContent = app.strings["job.done"][i].replace("%@", app.strings["tool.removebg"][i]);
    $$(".dest").forEach((b) => b.setAttribute("aria-pressed", b.dataset.dest === dest));
  }
  $$(".dest").forEach((b) => b.addEventListener("click", () => { dest = b.dataset.dest; renderDest(); }));

  // ---- language showcase: the wheel's own words, straight from the app
  const SHOW = ["en", "ko", "ja", "zh-Hans", "zh-Hant", "es", "fr", "de", "ru", "pt-BR"];
  const NAMES = { en: "English", ko: "한국어", ja: "日本語", "zh-Hans": "简体中文", "zh-Hant": "繁體中文", es: "Español", fr: "Français", de: "Deutsch", ru: "Русский", "pt-BR": "Português" };
  let shown = null;
  const langsBox = $("#langs");
  SHOW.forEach((l) => { const b = document.createElement("button"); b.type = "button"; b.lang = l; b.textContent = NAMES[l]; b.dataset.l = l; langsBox.appendChild(b); });
  langsBox.addEventListener("click", (e) => { const b = e.target.closest("button"); if (b) { shown = b.dataset.l; renderLangShow(); } });
  function renderLangShow() {
    const l = shown || lang, app = window.APP, i = app.languages.indexOf(l);
    $$("#langs button").forEach((b) => b.setAttribute("aria-pressed", b.dataset.l === l));
    const keys = ["mode.convert", "mode.tools", "mode.actions", "tool.removebg", "tool.ocr", "tool.rotate", "dest.clipboard", "job.in_clipboard", "card.reveal", "card.undo"];
    const box = $("#lang-show"); box.lang = l;
    box.innerHTML = keys.map((k) => '<span class="chip">' + app.strings[k][i] + "</span>").join("");
  }

  // ---- film
  const video = $("#film-video"), player = $("#player");
  function setFilm() {
    const want = "assets/media/film-" + (lang === "ko" ? "ko" : "en") + ".mp4";
    const src = video.querySelector("source");
    if (src.getAttribute("src") !== want) {
      src.setAttribute("src", want); video.poster = "assets/media/poster-" + (lang === "ko" ? "ko" : "en") + ".webp"; video.load();
      player.classList.remove("playing"); video.controls = false;
    }
  }
  // Native controls appear once the film starts, so the poster stays clean.
  $("#play").addEventListener("click", () => { video.controls = true; player.classList.add("playing"); video.play(); });
  video.addEventListener("play", () => { video.controls = true; player.classList.add("playing"); });

  // ---- latest release from GitHub (falls back to the releases page)
  let release = null;
  function renderRelease() {
    if (!release) return;
    $$(".dl-version").forEach((n) => (n.textContent = X("dl.version", release.version)));
    $$(".dl-version-long").forEach((n) => (n.textContent = X("dl.size", "v" + release.version, release.size)));
  }
  fetch("https://api.github.com/repos/ivLis-Studio/pickverter/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
    .then((r) => (r.ok ? r.json() : null))
    .then((j) => {
      if (!j) return;
      const dmg = (j.assets || []).find((a) => /\.dmg$/i.test(a.name));
      const sums = (j.assets || []).find((a) => /SHA256SUMS/i.test(a.name));
      release = { version: j.tag_name.replace(/^v/, ""), size: dmg ? (dmg.size / 1048576).toFixed(1) : "—" };
      if (dmg) $$(".dl-link").forEach((a) => (a.href = dmg.browser_download_url));
      if (sums) $$(".dl-sums").forEach((a) => (a.href = sums.browser_download_url));
      renderRelease();
    })
    .catch(() => {});

  // ---- copy brew command
  $("#copy-brew").addEventListener("click", async (e) => {
    try { await navigator.clipboard.writeText($("#brew-cmd").textContent); } catch { return; }
    const span = e.currentTarget.querySelector("span[data-t]"), old = span.textContent;
    span.textContent = X("copied"); setTimeout(() => (span.textContent = old), 1400);
  });

  applyLang(lang);
})();


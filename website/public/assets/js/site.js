// Page behaviour: language, platform (Mac or Windows), icons, reveal, tabs, comparison slider, destinations, film, release info.
(function () {
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const LANGS = ["en", "ko"];
  const OSES = ["mac", "win"];
  const RELEASES = "https://github.com/ivLis-Studio/pickverter/releases/latest";
  const STORE = "https://apps.microsoft.com/detail/9P05JBV4QDGC";
  const stored = (k) => { try { return localStorage.getItem(k); } catch { return null; } };
  const keep = (k, v) => { try { localStorage.setItem(k, v); } catch {} };

  // ---- icons
  const paint = (n) => { n.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true">' + (window.ICONS[n.dataset.i] || "") + "</svg>"; };
  $$("[data-i]").forEach(paint);

  // ---- language: ?lang= → saved → browser
  const params = new URLSearchParams(location.search);
  const browser = (navigator.languages || [navigator.language || "en"]).map((l) => l.slice(0, 2).toLowerCase()).find((l) => LANGS.includes(l));
  let lang = [params.get("lang"), stored("pv.lang"), browser, "en"].find((l) => LANGS.includes(l));

  // ---- platform: ?os= → saved → this computer
  function systemOS() {
    const platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
    return /win/i.test(platform) || /Windows/.test(navigator.userAgent) ? "win" : "mac";
  }
  let os = [params.get("os"), stored("pv.os"), systemOS()].find((o) => OSES.includes(o));
  let arm = false; // a Windows PC on Arm gets the Arm64 installer first

  const originals = new Map($$("[data-t]").map((n) => [n, n.innerHTML]));
  const X = (k, ...a) => ((window.SITE_EXTRA[lang] || window.SITE_EXTRA.en)[k] || window.SITE_EXTRA.en[k] || k).replace(/%@/g, () => a.shift());
  // Chains like "Excel·Numbers·PowerPoint" may break after a dot instead of mid-word on a narrow phone.
  const dotBreaks = (h) => h.replace(/(<[^>]*>)|·(?=\S)/g, (m, tag) => tag || "·<wbr>");

  // English copy lives in index.html, Korean in SITE_I18N; on the Windows page SITE_WIN replaces what differs.
  function applyLang(l) {
    lang = l;
    document.documentElement.lang = l;
    const dict = window.SITE_I18N[l] || {}, win = os === "win" ? window.SITE_WIN[l] || {} : {};
    for (const [n, en] of originals) {
      const k = n.dataset.t;
      n.innerHTML = dotBreaks(k in win ? win[k] : l === "en" ? en : dict[k] ?? en);
    }
    $$("[data-t] [data-i], [data-i]").forEach((n) => { if (!n.firstChild) paint(n); });
    const meta = window.SITE_META[l] || window.SITE_META.en;
    document.title = meta.title;
    $('meta[name="description"]').setAttribute("content", meta.desc);
    $("#lang-select").value = l;
    $("#play").setAttribute("aria-label", X("film.play"));
    $("#wheel").setAttribute("aria-label", X("wheel.label"));
    $("#os-switch").setAttribute("aria-label", X("os.label"));
    setFilm();
    if (wheel) wheel.setLang(l, os), wheel.setLabels({ done: X("demo.done") });
    renderRelease();
    renderDest();
    renderLangShow();
  }
  $("#lang-select").addEventListener("change", (e) => {
    keep("pv.lang", e.target.value);
    const u = new URL(location.href); u.searchParams.set("lang", e.target.value); history.replaceState(null, "", u);
    applyLang(e.target.value);
  });

  function applyOS(o) {
    os = o;
    document.documentElement.dataset.os = o;
    $$("#os-switch button").forEach((b) => b.setAttribute("aria-pressed", String(b.dataset.os === o)));
    $$("[data-src-win]").forEach((img) => {
      if (!img.dataset.srcMac) img.dataset.srcMac = img.getAttribute("src");
      img.setAttribute("src", o === "win" ? img.dataset.srcWin : img.dataset.srcMac);
    });
    $$("[data-href-win]").forEach((a) => {
      if (!a.dataset.hrefMac) a.dataset.hrefMac = a.getAttribute("href");
      a.setAttribute("href", o === "win" ? a.dataset.hrefWin : a.dataset.hrefMac);
    });
    $$(".os-other").forEach((a) => a.setAttribute("href", "?os=" + (o === "win" ? "mac" : "win")));
    applyLang(lang);
  }
  function chooseOS(o) {
    keep("pv.os", o);
    const u = new URL(location.href); u.searchParams.set("os", o); history.replaceState(null, "", u);
    applyOS(o);
  }
  $$("#os-switch button").forEach((b) => b.addEventListener("click", () => chooseOS(b.dataset.os)));
  $$(".os-other").forEach((a) => a.addEventListener("click", (e) => { e.preventDefault(); chooseOS(os === "win" ? "mac" : "win"); }));

  // ---- header border on scroll
  const bar = $("#bar");
  addEventListener("scroll", () => bar.classList.toggle("scrolled", scrollY > 8), { passive: true });

  // ---- reveal on scroll
  const io = new IntersectionObserver((es) => es.forEach((e) => { if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); } }), { rootMargin: "0px 0px -8% 0px" });
  $$(".reveal").forEach((n) => io.observe(n));

  // ---- live wheel
  let wheel = null;
  wheel = window.PickWheel.mount($("#wheel"), { lang, os, file: "Lisbon.heic", done: X("demo.done"), live: $("#wheel-live") });

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

  // ---- language showcase: the wheel's own words, straight from the app (the Windows app's on the Windows page)
  const SHOW = ["en", "ko", "ja", "zh-Hans", "zh-Hant", "es", "fr", "de", "ru", "pt-BR"];
  const NAMES = { en: "English", ko: "한국어", ja: "日本語", "zh-Hans": "简体中文", "zh-Hant": "繁體中文", es: "Español", fr: "Français", de: "Deutsch", ru: "Русский", "pt-BR": "Português" };
  let shown = null;
  const langsBox = $("#langs");
  SHOW.forEach((l) => { const b = document.createElement("button"); b.type = "button"; b.lang = l; b.textContent = NAMES[l]; b.dataset.l = l; langsBox.appendChild(b); });
  langsBox.addEventListener("click", (e) => { const b = e.target.closest("button"); if (b) { shown = b.dataset.l; renderLangShow(); } });
  function renderLangShow() {
    const l = shown || lang, app = window.APP, i = app.languages.indexOf(l), W = os === "win" ? window.APP_WIN.strings : {};
    $$("#langs button").forEach((b) => b.setAttribute("aria-pressed", b.dataset.l === l));
    const keys = ["mode.convert", "mode.tools", "mode.actions", "tool.removebg", "tool.ocr", "tool.rotate", "dest.clipboard", "job.in_clipboard", "card.reveal", "card.undo"];
    const box = $("#lang-show"); box.lang = l;
    box.innerHTML = keys.map((k) => '<span class="chip">' + (W[k] || app.strings[k])[i] + "</span>").join("");
  }

  // ---- film: the Mac or the Windows cut, in English or Korean.
  // While it's on screen it plays muted and looping (not with reduced motion or Data Saver, where the play button stays);
  // Sound on, or a click on the picture, starts it over with sound and the native controls.
  const video = $("#film-video"), player = $("#player"), pauseButton = $("#film-pause");
  const calm = matchMedia("(prefers-reduced-motion: reduce)").matches || !!(navigator.connection && navigator.connection.saveData);
  let ambient = false, held = false, inView = false;
  function setPauseButton() {
    if (!pauseButton) return;
    const icon = pauseButton.querySelector(".ico");
    const want = video.paused ? "play" : "pause";
    if (icon && icon.dataset.i !== want) { icon.dataset.i = want; paint(icon); }
    pauseButton.setAttribute("aria-label", X(video.paused ? "film.resume" : "film.pause"));
  }
  function quietPlay() {
    const started = video.play();
    // Autoplay refused (an iPhone in Low Power Mode, a strict browser setting): show the poster and the play button.
    if (started && started.catch) started.catch((e) => { if (e && e.name === "NotAllowedError") { ambient = false; player.classList.remove("ambient"); } });
  }
  function enterAmbient() {
    ambient = !calm; held = false;
    video.muted = true; video.loop = true; video.controls = false;
    player.classList.remove("playing");
    player.classList.toggle("ambient", ambient);
    if (ambient && inView) quietPlay();
    setPauseButton();
  }
  function playWithSound() {
    ambient = false; held = false;
    player.classList.remove("ambient"); player.classList.add("playing");
    video.loop = false; video.muted = false; video.controls = true;
    try { video.currentTime = 0; } catch {}
    const started = video.play();
    if (started && started.catch) started.catch(() => {});
  }
  function setFilm() {
    const name = (os === "win" ? "win-" : "") + (lang === "ko" ? "ko" : "en");
    const want = "assets/media/film-" + name + ".mp4";
    const src = video.querySelector("source");
    if (src.getAttribute("src") !== want) {
      src.setAttribute("src", want); video.poster = "assets/media/poster-" + name + ".webp"; video.load();
      enterAmbient();
    }
    setPauseButton();
  }
  new IntersectionObserver(([e]) => {
    inView = e.isIntersecting;
    if (!ambient || held) return;
    if (inView) quietPlay(); else video.pause();
  }, { threshold: 0.35 }).observe(player);
  $("#play").addEventListener("click", playWithSound);
  const soundButton = $("#film-sound");
  if (soundButton) soundButton.addEventListener("click", playWithSound);
  video.addEventListener("click", () => { if (ambient) playWithSound(); });
  if (pauseButton) pauseButton.addEventListener("click", () => {
    if (video.paused) { held = false; quietPlay(); } else { held = true; video.pause(); }
  });
  // Native controls appear once the film plays with sound, so the poster and the muted preview stay clean.
  video.addEventListener("play", () => { if (!ambient) { video.controls = true; player.classList.add("playing"); } setPauseButton(); });
  video.addEventListener("pause", setPauseButton);
  enterAmbient();

  // ---- Windows installs through the Store; GitHub supplies the Mac app and optional direct Windows downloads.
  let release = null;
  function renderRelease() {
    const r = release;
    const main = r && (os === "win" ? (arm && r.winArm) || r.winX64 : r.mac);
    const sums = r && (os === "win" ? r.winSums : r.macSums);
    $$(".dl-link").forEach((a) => (a.href = os === "win" ? STORE : main ? main.browser_download_url : RELEASES));
    $$(".dl-win-setup").forEach((a) => (a.href = r && ((arm && r.winArm) || r.winX64) ? ((arm && r.winArm) || r.winX64).browser_download_url : RELEASES));
    $$(".dl-sums").forEach((a) => (a.href = sums ? sums.browser_download_url : RELEASES));
    $$(".dl-win-arm").forEach((a) => (a.href = r && r.winArm ? r.winArm.browser_download_url : RELEASES));
    $$(".dl-win-zip").forEach((a) => (a.href = r && r.winZip ? r.winZip.browser_download_url : RELEASES));
    // GitHub and the Store can release at different times; don't label the Store with a GitHub version or file size.
    if (!main || os === "win") return;
    $$(".dl-version").forEach((n) => (n.textContent = X("dl.version", r.version)));
    $$(".dl-version-long").forEach((n) => (n.textContent = X("dl.size", "v" + r.version, (main.size / 1048576).toFixed(1))));
  }
  fetch("https://api.github.com/repos/ivLis-Studio/pickverter/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
    .then((r) => (r.ok ? r.json() : null))
    .then((j) => {
      if (!j) return;
      const assets = j.assets || [], find = (re) => assets.find((a) => re.test(a.name));
      release = {
        version: j.tag_name.replace(/^v/, ""),
        mac: find(/\.dmg$/i), macSums: find(/^SHA256SUMS\.txt$/i),
        winX64: find(/-win-x64-setup\.exe$/i), winArm: find(/-win-arm64-setup\.exe$/i),
        winZip: find(/-win-x64\.zip$/i), winSums: find(/^SHA256SUMS-windows\.txt$/i),
      };
      renderRelease();
    })
    .catch(() => {});
  if (systemOS() === "win" && navigator.userAgentData && navigator.userAgentData.getHighEntropyValues) {
    navigator.userAgentData.getHighEntropyValues(["architecture"]).then((v) => { arm = v.architecture === "arm"; renderRelease(); }).catch(() => {});
  }

  applyOS(os);
})();

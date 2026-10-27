#!/usr/bin/env python3
"""Checks docs/store/microsoft-listing.json against Partner Center's limits and the listing's own rules.

    python3 docs/store/check-listing.py [path/to/listing.json]

Exits with 1 and lists every problem if anything is off. Lengths are counted in UTF-16 code units (what the Store's
fields count), which equals characters for this text. Limits: description 1,000-3,000 (our target; the Store allows
10,000) with a two-line hook, short description 270, 8-12 features of up to 200, up to 7 search terms of up to 30
characters and 21 words in all, 6 screenshot captions of up to 200 (one per screenshot, in order), trailer title 255,
copyright 200, developed by 255, additional license terms 10,000 on one line.

Every description also says, in the app's own words, that Remove BG's results come from an AI model and can be reported
from their card (the "AI result" label and its "Report" link): Microsoft Store policy 11.16 asks for that disclosure.
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
LISTING = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE / "microsoft-listing.json"

LANGS = {  # Store language code -> the app's language (macos/Resources/i18n.json)
    "en-us": "en", "ko-kr": "ko", "ja-jp": "ja", "zh-cn": "zh-Hans", "zh-tw": "zh-Hant",
    "es-es": "es", "fr-fr": "fr", "de-de": "de", "ru-ru": "ru", "pt-br": "pt-BR",
}
FIELDS = ["description", "shortDescription", "features", "searchTerms", "screenshotCaptions", "trailerTitle",
          "copyright", "developedBy", "additionalLicenseTerms"]
COPYRIGHT = "© 2026 ivLis STUDIO"
DEVELOPER = "ivLis STUDIO"
LICENSE_URL = "https://github.com/ivLis-Studio/pickverter/blob/main/LICENSE"
# promo/store.sh writes the screenshots in this order; the captions follow it.
SCREENSHOTS = ["01-webp-to-jpg", "02-word-to-pdf", "03-rotate", "04-remove-background", "05-paste-cutout", "06-share-to-phone"]
# The Windows edition only: nothing from the Mac app.
MAC_ONLY = re.compile(r"AirDrop|Finder|macOS|iCloud|iWork|Keynote|Spotlight|Quick Look|[⌥⌘⌃]|(?<![A-Za-z])Macs?(?![A-Za-z])")
MARKUP = re.compile(r"<[A-Za-z/!]|\*\*|^#|\]\(|&[a-z]+;", re.M)
# Every description names the gestures (with the keyboard's own key names), the optional downloads and the license.
KEYS = {"es-es": ["Mayús", "Ctrl", "Alt"], "fr-fr": ["Maj", "Ctrl", "Alt"], "de-de": ["Umschalt", "Strg", "Alt"]}
MENTIONS = ["Pickverter", "GitHub", "MIT", "ffmpeg", "rhwp", "Microsoft Store", "Microsoft Edge", "OCR"]
# The app's own words for the rings, two key tools and the AI label with its report link must appear in each description.
APP_TERMS = ["mode.convert", "mode.tools", "mode.actions", "tool.removebg", "tool.ocr", "ai.result", "ai.report"]


def units(text):
    return len(text.encode("utf-16-le")) // 2


def app_strings():
    base = ROOT / "macos/Resources/i18n.json"
    overlay = ROOT / "windows/Resources/i18n.windows.json"
    if not base.exists():
        return None
    data = json.loads(base.read_text(encoding="utf-8"))
    strings = {key: list(values) for key, values in data["strings"].items()}
    if overlay.exists():
        win = json.loads(overlay.read_text(encoding="utf-8"))
        for key, values in win["strings"].items():
            merged = strings.get(key, [None] * len(data["languages"]))
            for i, lang in enumerate(data["languages"]):
                j = win["languages"].index(lang) if lang in win["languages"] else -1
                if j >= 0 and values[j] is not None:
                    merged[i] = values[j]
            strings[key] = merged
    return data["languages"], strings


def main():
    listing = json.loads(LISTING.read_text(encoding="utf-8"))
    problems = []
    app = app_strings()

    def bad(code, message):
        problems.append(f"{code}: {message}")

    def text_field(code, name, value, low, high, one_line=True):
        if not isinstance(value, str) or not value.strip():
            bad(code, f"{name} is empty")
            return
        n = units(value)
        if not low <= n <= high:
            bad(code, f"{name} has {n} characters (allowed {low}-{high})")
        if value != value.strip():
            bad(code, f"{name} starts or ends with whitespace")
        if one_line and ("\n" in value or "\r" in value):
            bad(code, f"{name} must be one line")
        if "  " in value or " \n" in value:
            bad(code, f"{name} has a double space or a space before a line break")
        if MARKUP.search(value):
            bad(code, f"{name} looks like it contains markup (plain text only)")
        mac = MAC_ONLY.search(value)
        if mac:
            bad(code, f"{name} mentions '{mac.group(0)}', which is Mac-only")

    def text_list(code, name, values, low, high, limit):
        if not isinstance(values, list) or not low <= len(values) <= high:
            bad(code, f"{name} needs {low}-{high} items (has {len(values) if isinstance(values, list) else 'none'})")
            return
        for i, value in enumerate(values, 1):
            text_field(code, f"{name}[{i}]", value, 1, limit)
        if len({v.casefold() for v in values if isinstance(v, str)}) != len(values):
            bad(code, f"{name} has duplicates")

    missing = [code for code in LANGS if code not in listing]
    extra = [code for code in listing if code not in LANGS]
    if missing:
        problems.append("missing languages: " + ", ".join(missing))
    if extra:
        problems.append("unexpected languages: " + ", ".join(extra))

    shots = ROOT / "promo/out/store/screenshots"
    rows = []
    for code in [c for c in LANGS if c in listing]:
        entry = listing[code]
        for name in FIELDS:
            if name not in entry:
                bad(code, f"missing {name}")
        for name in entry:
            if name not in FIELDS:
                bad(code, f"unknown field {name}")
        d = entry.get("description", "")
        text_field(code, "description", d, 1000, 3000, one_line=False)
        lines = d.split("\n") if isinstance(d, str) else []
        if len(lines) < 4 or not lines[0].strip() or not lines[1].strip() or lines[2].strip():
            bad(code, "description must open with a two-line hook followed by a blank line")
        for word in KEYS.get(code, ["Shift", "Ctrl", "Alt"]) + MENTIONS:
            if word not in d:
                bad(code, f"description doesn't mention {word}")
        if app:
            languages, strings = app
            i = languages.index(LANGS[code])
            for key in APP_TERMS:
                term = strings[key][i]
                if term.casefold() not in d.casefold():
                    bad(code, f"description doesn't use the app's word for {key} ('{term}')")
        text_field(code, "shortDescription", entry.get("shortDescription", ""), 1, 270)
        text_list(code, "features", entry.get("features"), 8, 12, 200)
        terms = entry.get("searchTerms")
        text_list(code, "searchTerms", terms, 1, 7, 30)
        words = sum(len(t.split()) for t in terms) if isinstance(terms, list) else 0
        if words > 21:
            bad(code, f"searchTerms use {words} words (at most 21)")
        captions = entry.get("screenshotCaptions")
        text_list(code, "screenshotCaptions", captions, len(SCREENSHOTS), len(SCREENSHOTS), 200)
        text_field(code, "trailerTitle", entry.get("trailerTitle", ""), 1, 255)
        if entry.get("copyright") != COPYRIGHT:
            bad(code, f"copyright must be '{COPYRIGHT}'")
        text_field(code, "copyright", entry.get("copyright", ""), 1, 200)
        if entry.get("developedBy") != DEVELOPER:
            bad(code, f"developedBy must be '{DEVELOPER}'")
        text_field(code, "developedBy", entry.get("developedBy", ""), 1, 255)
        terms_line = entry.get("additionalLicenseTerms", "")
        text_field(code, "additionalLicenseTerms", terms_line, 1, 10000)
        if LICENSE_URL not in terms_line:
            bad(code, "additionalLicenseTerms must link " + LICENSE_URL)
        folder = shots / code.split("-")[0]
        if folder.is_dir():
            names = sorted(p.stem for p in folder.glob("*.png"))
            if names != SCREENSHOTS:
                bad(code, f"{folder.relative_to(ROOT)} holds {names}, expected {SCREENSHOTS}")
        feats = entry.get("features") or []
        caps = captions or []
        rows.append((code, units(d), units(entry.get("shortDescription", "")),
                     f"{len(feats)} ≤{max(map(units, feats), default=0)}", f"{len(terms or [])}/{words}w ≤{max(map(units, terms or []), default=0)}",
                     f"{len(caps)} ≤{max(map(units, caps), default=0)}", units(entry.get("trailerTitle", ""))))

    print(f"{'lang':7} {'descr':>5} {'short':>5} {'features':>9} {'terms':>11} {'captions':>9} {'trailer':>7}")
    for code, desc, short, feats, terms, caps, trailer in rows:
        print(f"{code:7} {desc:>5} {short:>5} {feats:>9} {terms:>11} {caps:>9} {trailer:>7}")
    if not app:
        print("(app strings not found: skipped the terminology check)")
    if problems:
        print(f"\n{len(problems)} problem(s):")
        for p in problems:
            print("  - " + p)
        return 1
    print("\nAll checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

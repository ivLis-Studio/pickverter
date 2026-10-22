#!/usr/bin/env python3
"""Checks the Windows strings: macos/Resources/i18n.json (shared) plus windows/Resources/i18n.windows.json (overlay).

  * every key the Windows sources use exists, with a value for every language
  * overlay placeholders (%@, %1$@ …) match English, and match the shared table when a key is overridden
  * plural forms stay within what the language needs
  * no Mac wording (Finder, ⌘, ⌥, Trash, brew…) reaches a string the Windows app shows

Usage: scripts/check-i18n.py
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASE = json.loads((ROOT.parent / "macos/Resources/i18n.json").read_text(encoding="utf-8"))
OVERLAY = json.loads((ROOT / "Resources/i18n.windows.json").read_text(encoding="utf-8"))
LANGS = BASE["languages"]

# Keys built at runtime from enum cases (prefix + raw value).
DYNAMIC = {
    "mode": ["convert", "tools", "actions"],
    "dest": ["file", "clipboard", "both", "ask"],
    "rot": ["right", "left", "half", "flipH", "flipV"],
    "bg": ["transparent", "crop", "white"],
    "path": ["full", "quoted", "name", "unix"],
    "q": ["balanced", "high", "small"],
    "lufs": ["podcast", "stream", "broadcast"],
    "frame": ["middle", "first", "last"],
    "redact": ["pixelate", "blur", "fill"],
    "hud": ["topRight", "bottomRight", "topLeft", "bottomLeft"],
    "meta": ["title", "author", "copyright", "date", "keywords", "subject", "creator", "artist", "album", "genre", "comment", "year"],
    "collage": ["grid", "row", "column"],
    "viz": ["wave", "square", "spectrum"],
}

MAC_WORDING = re.compile(r"Finder|⌘|⌥|⌃|↩︎|\bMac\b|macOS|brew install|AirDrop|TextEdit|Keynote|iWork|\bTrash\b|Papelera\b(?! de reciclaje)|废纸篓|垃圾桶|ゴミ箱|\bLixo\b|esc closes")


def table():
    merged = {}
    base_column = {code: index for index, code in enumerate(LANGS)}
    for key, row in BASE["strings"].items():
        merged[key] = dict(zip(LANGS, row))
    for key, row in OVERLAY["strings"].items():
        entry = merged.setdefault(key, {})
        for code, value in zip(OVERLAY["languages"], row):
            if value and code in base_column:
                entry[code] = value
    return merged


def used_keys():
    keys = set()
    for path in (ROOT / "src").rglob("*.cs"):
        if "/bin/" in path.as_posix() or "/obj/" in path.as_posix():
            continue
        text = path.read_text(encoding="utf-8")
        keys.update(k for k in re.findall(r'\bLP?\(\s*"([a-z0-9_.\-]+)"', text) if not k.endswith("."))
        for a, b in re.findall(r'LP?\([^"\n]*\? "([a-z0-9_.]+)" : "([a-z0-9_.]+)"', text):
            keys.update([a, b])
        for match in re.finditer(r'\bTool\("([a-z0-9]+)"([^\n]*)', text):
            name, rest = match.groups()
            keys.add("tool." + name)
            if "detail:" not in rest:
                keys.add("tool." + name + ".d")
    for prefix, cases in DYNAMIC.items():
        keys.update(f"{prefix}.{case}" for case in cases)
    return keys


def placeholders(text):
    return sorted(re.findall(r"%(?:\d+\$)?@", text))


def main():
    problems = []
    merged = table()
    used = used_keys()
    for key in sorted(used):
        if key not in merged:
            problems.append(f"missing key: {key}")
            continue
        for lang in LANGS:
            value = merged[key].get(lang, "")
            if not value.strip():
                problems.append(f"{key} [{lang}]: empty")
            elif MAC_WORDING.search(value):
                problems.append(f"{key} [{lang}]: Mac wording: {value!r}")
    for key, row in OVERLAY["strings"].items():
        if len(row) != len(OVERLAY["languages"]):
            problems.append(f"overlay {key}: {len(row)} entries for {len(OVERLAY['languages'])} languages")
            continue
        english = row[OVERLAY["languages"].index("en")]
        reference = BASE["strings"].get(key, [english])[0]
        if key in BASE["strings"] and set(placeholders(english)) != set(placeholders(reference)):
            problems.append(f"overlay {key}: placeholders {placeholders(english)} differ from the shared table {placeholders(reference)}")
        base_forms = english.split("|")
        for lang, value in zip(OVERLAY["languages"], row):
            if not value.strip():
                problems.append(f"overlay {key} [{lang}]: empty")
                continue
            forms = value.split("|")
            if len(forms) > 1 or len(base_forms) > 1:
                limit = 3 if lang == "ru" else 2
                if len(forms) > limit:
                    problems.append(f"overlay {key} [{lang}]: {len(forms)} plural forms")
                expected = set(placeholders(base_forms[-1]))
                for form in forms:
                    if set(placeholders(form)) - expected:
                        problems.append(f"overlay {key} [{lang}]: placeholders {placeholders(form)} vs {sorted(expected)}")
            elif placeholders(value) != placeholders(english):
                problems.append(f"overlay {key} [{lang}]: placeholders {placeholders(value)} vs {placeholders(english)}")
    unused = sorted(set(OVERLAY["strings"]) - used)
    for line in problems:
        print(line)
    if unused:
        print("overlay keys no source uses: " + ", ".join(unused))
    print(f"{len(used)} keys used · {len(OVERLAY['strings'])} overlay keys × {len(OVERLAY['languages'])} languages · {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())


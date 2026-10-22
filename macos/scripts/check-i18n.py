#!/usr/bin/env python3
"""Checks Resources/i18n.json against the sources.

  * every key the code uses exists, with a non-empty string for every language
  * placeholders (%@, %1$@, %2$@ …) match English in every language
  * plural forms ("a|b|c") stay within what the language needs

Usage: scripts/check-i18n.py [--missing]   (--missing prints only the missing keys)
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
TABLE = json.loads((ROOT / "Resources/i18n.json").read_text())
LANGS = TABLE["languages"]
STRINGS = TABLE["strings"]

# Keys built at runtime from enum cases (prefix + rawValue).
DYNAMIC = {
    "mode": ["convert", "tools", "actions"],
    "dest": ["file", "clipboard", "both", "ask"],
    "rot": ["right", "left", "half", "flipH", "flipV"],
    "bg": ["transparent", "crop", "white"],
    "path": ["full", "tilde", "name", "quoted"],
    "q": ["balanced", "high", "small"],
    "lufs": ["podcast", "stream", "broadcast"],
    "frame": ["middle", "first", "last"],
    "redact": ["pixelate", "blur", "fill"],
    "hud": ["topRight", "bottomRight", "topLeft", "bottomLeft"],
    "meta": ["title", "author", "copyright", "date", "keywords", "subject", "creator", "artist", "album", "genre", "comment"],
    "collage": ["grid", "row", "column"],
    "viz": ["wave", "square", "spectrum"],
    "office.iwork": ["pages", "keynote", "numbers"],
    "access.state": ["granted", "denied", "unknown"],
}


def used_keys():
    keys = set()
    for path in (ROOT / "Sources").rglob("*.swift"):
        text = path.read_text()
        keys.update(k for k in re.findall(r'\bLP?\("([a-z0-9_.]+)"', text) if not k.endswith("."))
        for match in re.finditer(r'\btool\("([a-z0-9]+)"([^\n]*)', text):
            name, rest = match.groups()
            keys.add("tool." + name)
            if "detail:" not in rest:
                keys.add("tool." + name + ".d")
    for prefix, cases in DYNAMIC.items():
        keys.update(f"{prefix}.{case}" for case in cases)
    # Ternaries such as L(many ? "tool.zip.many" : "tool.zip").
    for path in (ROOT / "Sources").rglob("*.swift"):
        for a, b in re.findall(r'LP?\([^"\n]*\? "([a-z0-9_.]+)" : "([a-z0-9_.]+)"', path.read_text()):
            keys.update([a, b])
    return keys


def placeholders(text):
    return sorted(re.findall(r"%(?:\d+\$)?@", text))


def main():
    only_missing = "--missing" in sys.argv
    problems = []
    missing = sorted(k for k in used_keys() if k not in STRINGS)
    for key in missing:
        problems.append(f"missing key: {key}")
    for key, row in STRINGS.items():
        if len(row) != len(LANGS):
            problems.append(f"{key}: {len(row)} entries for {len(LANGS)} languages")
            continue
        english = row[0]
        base_forms = english.split("|")
        for lang, value in zip(LANGS, row):
            if not value.strip():
                problems.append(f"{key} [{lang}]: empty")
                continue
            forms = value.split("|")
            if len(forms) > 1 or len(base_forms) > 1:
                limit = 3 if lang == "ru" else 2
                if len(forms) > limit:
                    problems.append(f"{key} [{lang}]: {len(forms)} plural forms")
                expected = placeholders(base_forms[-1])
                for form in forms:
                    if placeholders(form) != expected and set(placeholders(form)) - set(expected):
                        problems.append(f"{key} [{lang}]: placeholders {placeholders(form)} vs {expected}")
            elif placeholders(value) != placeholders(english):
                problems.append(f"{key} [{lang}]: placeholders {placeholders(value)} vs {placeholders(english)}")
    if only_missing:
        print("\n".join(missing))
        return 0
    for line in problems:
        print(line)
    print(f"{len(STRINGS)} keys × {len(LANGS)} languages · {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())

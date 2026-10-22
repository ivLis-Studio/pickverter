#!/usr/bin/env python3
"""Subsets Fluent UI System Icons (MIT) for the Windows cut of the promo.

Takes every SF Symbol name the Windows app maps (windows/scripts/icons.json, resolved exactly like
windows/scripts/make-glyphs.mjs) plus the chrome the stage draws around it (File Explorer, the share sheet, the phone),
and writes:
  assets/fluent/FluentPromo-Regular.ttf, FluentPromo-Filled.ttf, LICENSE
  src/win/glyphs.js   PV.GLYPHS = { name: [filled, codepoint] }

  FLUENT_DIR=/path/to/fonts .venv/bin/python tools/fluent-glyphs.py
FLUENT_DIR holds FluentSystemIcons-Regular/Filled .ttf and .json from
https://github.com/microsoft/fluentui-system-icons/tree/main/fonts (default /tmp/fluent).
"""
import json
import os
import shutil
from pathlib import Path

from fontTools import subset

HERE = Path(__file__).resolve().parent.parent
FLUENT = Path(os.environ.get("FLUENT_DIR", "/tmp/fluent"))
SIZES = [20, 24, 16, 28, 32, 48, 12]

# Stage chrome: "f." names are drawn with the regular style, "fill:" picks the filled one.
EXTRA = {
    "f.desktop": ["desktop"], "f.download": ["arrow_download"], "f.document": ["document"], "f.image": ["image"],
    "f.music": ["music_note_2"], "f.video": ["video"], "f.home": ["home"], "f.gallery": ["image_multiple"],
    "f.pc": ["desktop"], "f.back": ["arrow_left"], "f.forward": ["arrow_right"], "f.up": ["arrow_up"],
    "f.refresh": ["arrow_clockwise"], "f.chevron": ["chevron_right"], "f.chevron.down": ["chevron_down"],
    "f.search": ["search"], "f.add": ["add"], "f.close": ["dismiss"], "f.min": ["subtract"], "f.max": ["square"],
    "f.new": ["add_circle"], "f.cut": ["cut"], "f.copy": ["copy"], "f.paste": ["clipboard_paste"],
    "f.rename": ["rename"], "f.share": ["share"], "f.delete": ["delete"], "f.sort": ["arrow_sort"],
    "f.view": ["grid"], "f.more": ["more_horizontal"], "f.details": ["text_bullet_list_ltr"], "f.tiles": ["grid"],
    "f.phone": ["phone"], "f.phonelink": ["phone_laptop"], "f.mail": ["mail"], "f.people": ["people"],
    "f.send": ["send"], "f.pdf": ["document_pdf"], "f.emoji": ["emoji"], "f.attach": ["attach"], "f.call": ["call"],
    "f.person": ["person"], "f.folder": ["folder"], "f.link": ["link"], "f.laptop": ["laptop"],
    "f.check": ["checkmark_circle"], "f.open": ["open"], "f.pin": ["pin"], "f.star": ["star"],
    "fill.desktop": ["fill:desktop"], "fill.download": ["fill:arrow_download"], "fill.document": ["fill:document"],
    "fill.image": ["fill:image"], "fill.music": ["fill:music_note_2"], "fill.video": ["fill:video"],
    "fill.home": ["fill:home"], "fill.gallery": ["fill:image_multiple"], "fill.folder": ["fill:folder"],
    "fill.phone": ["fill:phone"], "fill.phonelink": ["fill:phone_laptop"], "fill.mail": ["fill:mail"],
    "fill.pdf": ["fill:document_pdf"], "fill.send": ["fill:send"], "fill.check": ["fill:checkmark_circle"],
    "fill.wifi": ["fill:wifi_1"], "fill.cell": ["fill:cellular_data_1"], "fill.battery": ["fill:battery_10"],
    "fill.play": ["fill:play"], "fill.person": ["fill:person"], "fill.laptop": ["fill:laptop"],
    "fill.stop": ["fill:stop"], "fill.star": ["fill:star"],
}


def main():
    icons = json.loads((HERE.parent / "windows/scripts/icons.json").read_text())
    tables = {
        "regular": json.loads((FLUENT / "FluentSystemIcons-Regular.json").read_text()),
        "filled": json.loads((FLUENT / "FluentSystemIcons-Filled.json").read_text()),
    }

    def resolve(candidate):
        filled = candidate.startswith("fill:")
        name = candidate[5:] if filled else candidate
        style = "filled" if filled else "regular"
        for size in SIZES:
            code = tables[style].get(f"ic_fluent_{name}_{size}_{style}")
            if code is not None:
                return style, code
        return None

    used = {"regular": set(), "filled": set()}
    table, missing = {}, []
    for name, candidates in list(icons.items()) + list(EXTRA.items()):
        hit = next((h for h in map(resolve, candidates) if h), None)
        if not hit:
            missing.append(name)
            continue
        style, code = hit
        used[style].add(code)
        table[name] = [1 if style == "filled" else 0, code]
    if missing:
        raise SystemExit("no Fluent glyph for: " + ", ".join(missing))

    out = HERE / "assets/fluent"
    out.mkdir(parents=True, exist_ok=True)
    for style, file in (("regular", "Regular"), ("filled", "Filled")):
        options = subset.Options()
        options.layout_features = []
        options.hinting = False
        options.desubroutinize = True
        options.notdef_outline = True
        font = subset.load_font(str(FLUENT / f"FluentSystemIcons-{file}.ttf"), options)
        subsetter = subset.Subsetter(options)
        subsetter.populate(unicodes=sorted(used[style]))
        subsetter.subset(font)
        subset.save_font(font, str(out / f"FluentPromo-{file}.ttf"), options)
    shutil.copyfile(FLUENT / "LICENSE", out / "LICENSE")

    body = ",\n".join(f'    "{name}": [{filled}, 0x{code:X}]' for name, (filled, code) in sorted(table.items()))
    (HERE / "src/win").mkdir(parents=True, exist_ok=True)
    (HERE / "src/win/glyphs.js").write_text(
        "// Generated by tools/fluent-glyphs.py from windows/scripts/icons.json and the stage chrome. Do not edit.\n"
        "// Fluent UI System Icons (MIT): assets/fluent/LICENSE.\n"
        "PV.GLYPHS = {\n" + body + "\n};\n")
    print(f"{len(table)} glyphs · regular {len(used['regular'])} · filled {len(used['filled'])}")


if __name__ == "__main__":
    main()


#!/usr/bin/env python3
"""Fill a Partner Center listing export with docs/store/microsoft-listing.json and stage it for "Import folder".

    python3 docs/store/build-listing-csv.py EXPORT.csv [--assets promo/out/store] [--out promo/out/store/import] [--no-trailers]

EXPORT.csv is the file Partner Center gives you from "Export listings" on the submission overview. Export it after the
package is uploaded, so it already has one column per language the package declares. Languages the export doesn't have
yet are added as new columns.

The result is OUT/pickverter-listing/: one CSV plus the screenshots, art and trailers it points at. Partner Center wants
paths that start with the folder's own name, so that folder is what you pick in "Import listings › Import folder".
Korean gets the Korean screenshots and trailer; every other language uses the English ones.

--no-trailers leaves the trailer rows empty and the videos out, for a quick first import (the two ~60 MB trailers can
then go up on their own, or through the listing page).

--text-only rewrites the words only (description, features, captions, search terms, trailer title) and keeps every
screenshot, logo and trailer cell exactly as exported, so the pictures and videos already in Partner Center stay. The
result is one CSV, OUT/pickverter-listing-text.csv, for "Import listings › Upload .csv".
"""
from __future__ import annotations

import argparse
import csv
import io
import json
import os
import shutil
import sys
from pathlib import Path

ROOT = "pickverter-listing"
REPO = Path(__file__).resolve().parents[2]
SHOTS = ["01-webp-to-jpg", "02-word-to-pdf", "03-rotate", "04-remove-background", "05-paste-cutout", "06-share-to-phone"]
ART = {  # CSV field -> file in <assets>/art (the larger size Partner Center accepts for each slot)
    "StoreLogo720x1080": "PosterArt-1440x2160.png",
    "StoreLogo1080x1080": "BoxArt-2160x2160.png",
    "StoreLogo300x300": "StoreLogo-300x300.png",
    "PromoImage1920x1080": "SuperHeroArt-3840x2160.png",
}


def listing_key(code: str, listing: dict) -> str | None:
    """Partner Center's language column (en-us, zh-hans-cn, pt-br, ...) -> a key of microsoft-listing.json."""
    c = code.strip().lower()
    if c in listing:
        return c
    if c.startswith("zh"):
        traditional = any(t in c for t in ("hant", "-tw", "-hk", "-mo"))
        return "zh-tw" if traditional else "zh-cn"
    base = c.split("-")[0]
    for key in listing:
        if key.split("-")[0] == base:
            return key
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("export", type=Path, help="CSV from Partner Center › Export listings")
    ap.add_argument("--listing", type=Path, default=REPO / "docs/store/microsoft-listing.json")
    ap.add_argument("--assets", type=Path, default=REPO / "promo/out/store")
    ap.add_argument("--out", type=Path, default=REPO / "promo/out/store/import")
    ap.add_argument("--no-trailers", action="store_true", help="leave the trailers out")
    ap.add_argument("--text-only", action="store_true", help="rewrite the words only; keep the exported media cells")
    args = ap.parse_args()

    listing = json.loads(args.listing.read_text(encoding="utf-8"))
    rows = list(csv.reader(io.StringIO(args.export.read_bytes().decode("utf-8-sig"))))
    header, body = rows[0], rows[1:]
    if header[:4] != ["Field", "ID", "Type (Type)", "default"]:
        sys.exit(f"unexpected header {header[:4]} — is this a Partner Center listing export?")

    while header and header[-1] == "":
        header.pop()
    langs = header[4:]
    have = {listing_key(c, listing) for c in langs}
    langs += [k for k in listing if k not in have]
    header = header[:4] + langs
    width = len(header)
    body = [r + [""] * (width - len(r)) if len(r) < width else r[:width] for r in body]

    dest = args.out if args.text_only else args.out / ROOT
    dest.mkdir(parents=True, exist_ok=True)
    if not args.text_only:
        stray = [p.name for p in dest.glob("*.csv") if p.name != f"{ROOT}.csv"]
        if stray:
            sys.exit(f"{dest} already has other CSV files ({', '.join(stray)}); Partner Center wants exactly one")

    used: set[str] = set()

    def asset(rel: str) -> str:
        src = args.assets / rel
        if not src.is_file():
            sys.exit(f"missing asset {src}")
        used.add(rel)
        return f"{ROOT}/{rel}"

    index = {r[0]: r for r in body if r and r[0]}

    def values(key: str, col: int) -> dict[str, str]:
        item = listing[key]
        shots = "ko" if key == "ko-kr" else "en"
        film = "ko" if key == "ko-kr" else "en"
        v = {
            "Description": item["description"],
            "ShortDescription": item["shortDescription"],
            "DevStudio": item["developedBy"],
            "CopyrightTrademarkInformation": item["copyright"],
            "AdditionalLicenseTerms": item["additionalLicenseTerms"],
        }
        if args.text_only:
            # Media cells stay as exported (Partner Center URLs); a trailer's title only where that trailer is.
            if index.get("Trailer1", [""] * width)[col]:
                v["TrailerTitle1"] = item["trailerTitle"]
        elif not args.no_trailers:
            v["TrailerTitle1"] = item["trailerTitle"]
            v["Trailer1"] = asset(f"trailer/Pickverter-trailer-{film}.mp4")
            v["TrailerThumbnail1"] = asset(f"trailer/Pickverter-trailer-{film}-thumbnail.png")
            v["TrailerToPlayAtTopOfListing"] = v["Trailer1"]
        if not args.text_only:
            for field, name in ART.items():
                v[field] = asset(f"art/{name}")
        for i, (name, caption) in enumerate(zip(SHOTS, item["screenshotCaptions"]), 1):
            if not args.text_only:
                v[f"DesktopScreenshot{i}"] = asset(f"screenshots/{shots}/{name}.png")
            v[f"DesktopScreenshotCaption{i}"] = caption
        for i, feature in enumerate(item["features"], 1):
            v[f"Feature{i}"] = feature
        for i, term in enumerate(item["searchTerms"], 1):
            v[f"SearchTerm{i}"] = term
        return v

    for col, code in enumerate(langs, start=4):
        key = listing_key(code, listing)
        if key is None:
            print(f"! no copy for {code}; its column is left as exported", file=sys.stderr)
            continue
        for field, text in values(key, col).items():
            if field not in index:
                sys.exit(f"field {field} is not in the export")
            index[field][col] = text
        print(f"{code:<11} <- {key}")

    out = io.StringIO(newline="")
    csv.writer(out, lineterminator="\r\n").writerows([header] + body)
    if args.text_only:
        target = dest / f"{ROOT}-text.csv"
        target.write_bytes(b"\xef\xbb\xbf" + out.getvalue().encode("utf-8"))
        print(f"\n{target} — upload it in Import listings › Upload .csv (media cells kept as exported)")
        return 0

    for rel in sorted(used):
        target = dest / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(args.assets / rel, target)

    (dest / f"{ROOT}.csv").write_bytes(b"\xef\xbb\xbf" + out.getvalue().encode("utf-8"))
    size = sum((dest / rel).stat().st_size for rel in used)
    print(f"\n{dest}  ({len(used)} assets, {size / 1e6:.0f} MB) — pick this folder in Import listings › Import folder")
    return 0


if __name__ == "__main__":
    sys.exit(main())

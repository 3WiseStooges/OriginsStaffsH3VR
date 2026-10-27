"""Copy the Origins staff/crystal assets out of an OpenAssetTools dump of zm_tomb.ff.

Usage (from any directory):
    python collect_assets.py <oat_dump_dir> <oat_image_dump_dir> <out_dir>

<oat_dump_dir>        zone_dump/zm_tomb   (models, sounds, weapons; --model-format GLTF)
<oat_image_dump_dir>  zone_dump_img/zm_tomb (images, dumped with zm_tomb.ipak available)
<out_dir>             e.g. extracted/

Models keep their "../images/<name>.dds" references, and every referenced texture is
also written as PNG next to the DDS. Each GLTF gets a "_png" twin that points at the
PNGs so it opens in Blender, three.js and Unity GLTF importers without DDS support.
"""
import csv
import json
import re
import shutil
import sys
from pathlib import Path

from PIL import Image

MODEL_PATTERNS = [
    r"^t6_wpn_zmb_staff_.*_lod0\.gltf$",
    r"^t6_wpn_zmb_upg_crystal_.*_lod0\.gltf$",
    r"^p6_zm_tm_staff_holder_lod0\.gltf$",
    r"^p6_zm_tm_staff_projectile_ice_lod0\.gltf$",
]
SOUND_DIRS = ["wpn/energy/staff", "mus/zombie/tomb/staff", "zmb/level/zm_tomb/shovel_dig/crystal_collect"]
ALIAS_RE = re.compile(r"staff|crystal", re.I)


def png_name(dds_name):
    # OAT prefixes names with "~-g"/"~~-g" (streamed/gamma flags); drop them for readability.
    return re.sub(r"^~+-g", "", dds_name[:-4]).replace("~", "_") + ".png"


def convert(dds, png):
    try:
        with Image.open(dds) as im:
            im.load()
            im.save(png)
        return True
    except Exception as e:  # uncommon BC formats Pillow cannot decode
        print(f"  ! could not convert {dds.name}: {e}")
        return False


def main(dump, img_dump, out):
    dump, img_dump, out = Path(dump), Path(img_dump), Path(out)
    (out / "models").mkdir(parents=True, exist_ok=True)
    (out / "images").mkdir(exist_ok=True)

    textures = set()
    models = sorted(
        p for p in (dump / "model_export").glob("*.gltf")
        if any(re.match(pat, p.name) for pat in MODEL_PATTERNS)
    )
    for src in models:
        gltf = json.loads(src.read_text())
        for img in gltf.get("images", []):
            textures.add(Path(img["uri"]).name)
        shutil.copy2(src, out / "models" / src.name)
    print(f"models: {len(models)}")

    converted = {}
    for name in sorted(textures):
        src = img_dump / "images" / name
        if not src.exists():
            print(f"  ! missing texture {name}")
            continue
        shutil.copy2(src, out / "images" / name)
        png = out / "images" / png_name(name)
        if convert(src, png):
            converted[name] = png.name
    print(f"textures: {len(textures)} ({len(converted)} converted to PNG)")

    for src in models:
        gltf = json.loads(src.read_text())
        for img in gltf.get("images", []):
            name = Path(img["uri"]).name
            if name in converted:
                img["uri"] = "../images/" + converted[name]
                img.pop("mimeType", None)
        (out / "models" / src.name.replace("_lod0.gltf", "_png.gltf")).write_text(json.dumps(gltf))

    n_sounds = 0
    for rel in SOUND_DIRS:
        src = dump / "sound" / rel
        if src.exists():
            shutil.copytree(src, out / "sounds" / rel, dirs_exist_ok=True)
            n_sounds += sum(1 for f in src.rglob("*") if f.is_file())
    print(f"sounds: {n_sounds}")

    aliases = dump / "soundbank" / "zmb_tomb.all.aliases.csv"
    if aliases.exists():
        with aliases.open(newline="", encoding="utf-8", errors="replace") as f:
            rows = list(csv.reader(f))
        keep = [rows[0]] + [r for r in rows[1:] if r and ALIAS_RE.search(",".join(r[:2]))]
        with (out / "sounds" / "staff_aliases.csv").open("w", newline="", encoding="utf-8") as f:
            csv.writer(f).writerows(keep)
        print(f"sound aliases: {len(keep) - 1}")

    (out / "weapons").mkdir(exist_ok=True)
    weapons = sorted((dump / "weapons").glob("staff_*"))
    for w in weapons:
        shutil.copy2(w, out / "weapons" / w.name)
    print(f"weapon defs: {len(weapons)}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(__doc__)
    main(*sys.argv[1:])

"""Stage ripped staff models for the Unity project.

Usage:
    python export_unity.py <oat_obj_dump_dir> <extracted_dir> <unity_ripped_dir> <element> [<element> ...]

<oat_obj_dump_dir>  zone_dump_obj/zm_tomb (Unlinker --include-assets xmodel --model-format OBJ)
<extracted_dir>     output of collect_assets.py (GLTFs carry the tags and texture bindings)
<unity_ripped_dir>  Unity/Assets/OriginsStaffs/Ripped
<element>           fire | water | bolt | air

Unity 5.6 cannot import GLTF, so meshes go in as OAT's OBJ files. The GLTFs supply what
OBJ loses: attachment tags (baked to model space; StaffPrefabBuilder converts them to
Unity's left-handed axes) and which PNG each material uses. Both land in staff_assets.json, which
Editor/StaffPrefabBuilder.cs reads.
"""
import json
import math
import shutil
import sys
from pathlib import Path

# File-name stems differ per part: tips say "lightning", everything else says "bolt".
TIP_NAME = {"fire": "fire", "water": "water", "bolt": "lightning", "air": "air"}
UPG_TIP_NAME = {"fire": "fire", "water": "water", "bolt": "bolt", "air": "air"}


def models_for(el):
    return [
        "t6_wpn_zmb_staff_world",
        "t6_wpn_zmb_staff_upg_world",
        f"t6_wpn_zmb_staff_tip_{TIP_NAME[el]}_world",
        f"t6_wpn_zmb_staff_tip_upg_{UPG_TIP_NAME[el]}_world",
        f"t6_wpn_zmb_staff_crystal_{el}_world",
        f"t6_wpn_zmb_staff_reload_{el}_world",
        f"t6_wpn_zmb_staff_reload_upg_{el}_world",
        f"t6_wpn_zmb_staff_crystal_{el}_part",
    ]


def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qrot(q, v):
    x, y, z, w = qmul(qmul(q, (*v, 0.0)), (-q[0], -q[1], -q[2], q[3]))
    return (x, y, z)


def world_tags(gltf):
    """Model-space (position, rotation) of every tag_* node; OAT exports no scale."""
    nodes = gltf["nodes"]
    parent = {c: i for i, n in enumerate(nodes) for c in n.get("children", [])}

    def world(i):
        n = nodes[i]
        t = tuple(n.get("translation", (0.0, 0.0, 0.0)))
        r = tuple(n.get("rotation", (0.0, 0.0, 0.0, 1.0)))
        if i not in parent:
            return t, r
        pt, pr = world(parent[i])
        rt = qrot(pr, t)
        return (pt[0] + rt[0], pt[1] + rt[1], pt[2] + rt[2]), qmul(pr, r)

    def root_bone(i):
        # The bone directly under OAT's "<model>_skel" node carries OAT's Z-up -> Y-up turn.
        while i in parent and not nodes[parent[i]].get("name", "").endswith("_skel"):
            i = parent[i]
        return i

    tags = []
    for i, n in enumerate(nodes):
        if n.get("name", "").startswith("tag_"):
            (x, y, z), r = world(i)
            # OAT already converted the attached models' vertices to Y-up, so the root bone's
            # axis turn must not be applied to them a second time. The position keeps it,
            # because that is where the tag sits on the (also converted) staff mesh.
            rx, ry, rz, rw = nodes[root_bone(i)].get("rotation", (0.0, 0.0, 0.0, 1.0))
            qx, qy, qz, qw = qmul(r, (-rx, -ry, -rz, rw))
            # Right-handed, as in the OBJ. StaffPrefabBuilder mirrors these to match however
            # Unity actually imported the mesh.
            tags.append({"name": n["name"], "pos": [x, y, z], "rot": [qx, qy, qz, qw]})
    return tags


def unity_material_name(bo2_name):
    # "mc/mtl_t6_wpn_zmb_staff" -> "mtl_t6_wpn_zmb_staff": the "mc/" prefix is BO2's
    # shader-family folder, and "/" is not valid in a Unity asset name.
    return bo2_name.rsplit("/", 1)[-1]


def obj_materials(obj_path):
    return [line.split(None, 1)[1].strip() for line in obj_path.read_text().splitlines() if line.startswith("usemtl ")]


def main(obj_dump, extracted, out, elements):
    obj_dump, extracted, out = Path(obj_dump) / "model_export", Path(extracted), Path(out)
    (out / "Models").mkdir(parents=True, exist_ok=True)
    (out / "Textures").mkdir(exist_ok=True)

    names = []
    for el in elements:
        names += [n for n in models_for(el) if n not in names]

    entries = []
    for name in names:
        gltf_path = extracted / "models" / f"{name}_png.gltf"
        obj_path = obj_dump / f"{name}_lod0.obj"
        if not gltf_path.exists() or not obj_path.exists():
            print(f"  ! skipping {name}: missing {'GLTF' if not gltf_path.exists() else 'OBJ'}")
            continue
        gltf = json.loads(gltf_path.read_text())

        # OAT's OBJ points at a shared "zm_tomb.mtl" it never writes. Without a real .mtl,
        # Unity ignores "usemtl" and merges every surface into one submesh, so write a stub
        # .mtl per model. Its material names are what StaffPrefabBuilder matches on.
        lines = []
        for l in obj_path.read_text().splitlines():
            if l.startswith("mtllib "):
                l = f"mtllib {name}.mtl"
            elif l.startswith("usemtl "):
                l = "usemtl " + unity_material_name(l.split(None, 1)[1].strip())
            lines.append(l)
        text = "\n".join(lines)
        (out / "Models" / f"{name}.obj").write_text(text + "\n")
        (out / "Models" / f"{name}.mtl").write_text(
            "".join(f"newmtl {unity_material_name(m)}\nKd 1 1 1\n\n" for m in dict.fromkeys(obj_materials(obj_path))))

        images = [Path(i["uri"]).name for i in gltf.get("images", [])]
        textures = gltf.get("textures", [])
        by_name = {}
        for m in gltf.get("materials", []):
            def tex(slot):
                if not slot:
                    return ""
                png = images[textures[slot["index"]]["source"]]
                shutil.copy2(extracted / "images" / png, out / "Textures" / png)
                return png
            albedo = tex(m.get("pbrMetallicRoughness", {}).get("baseColorTexture"))
            lname = m["name"].lower()
            by_name[m["name"]] = {
                "name": unity_material_name(m["name"]),
                "albedo": albedo,
                "normal": tex(m.get("normalTexture")),
                # Crystal and power-source shaders are self-lit in BO2.
                "emissive": albedo if ("crystal" in lname or "pwr_srce" in lname) else "",
            }
        materials = [by_name[m] for m in obj_materials(obj_path)]
        verts = [tuple(map(float, l.split()[1:4])) for l in text.splitlines() if l.startswith("v ")]
        lo = [min(v[i] for v in verts) for i in range(3)]
        hi = [max(v[i] for v in verts) for i in range(3)]
        entries.append({"name": name, "obj": f"{name}.obj", "boundsMin": lo, "boundsMax": hi,
                        "materials": materials, "tags": world_tags(gltf)})
        print(f"  {name}: {len(materials)} material(s), {len(entries[-1]['tags'])} tag(s)")

    (out / "staff_assets.json").write_text(json.dumps({"models": entries}, indent=2))
    print(f"wrote {len(entries)} models to {out}")


if __name__ == "__main__":
    if len(sys.argv) < 5:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4:])

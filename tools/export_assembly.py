"""Build the high-detail, animatable staff assembly for Unity.

Usage (run with the OAT venv's python, which has Pillow and numpy):
    python export_assembly.py <oat_dump_dir> <oat_image_dump_dir> <extracted_dir> <unity_ripped_dir> <element> [...]

<oat_dump_dir>        zone_dump/zm_tomb (for xanim/, weapons/, sound/ and soundbank/)
<oat_image_dump_dir>  zone_dump_img/zm_tomb (for the effect textures and spawner icons)
<extracted_dir>       output of collect_assets.py (the *_png.gltf models and PNG textures)
<unity_ripped_dir>    Unity/Assets/OriginsStaffs/Ripped
<element>             fire, ice, lightning, wind (any number)

For each staff (base and upgraded) this writes Ripped/Assembly/Staff_<Element>[_Upgraded].json:
  * BO2's first-person staff (13k verts, front half), tip, head crystal and reload assembly,
    attached exactly as the weapon file does (tip/crystal on their tags, the reload model
    at X 8.409 on the root);
  * the world model's lower shaft and rubber handle, clipped and aligned onto it;
  * the skeleton (as transforms) and skin weights, so Unity can rebuild skinned meshes
    without importing any model file;
  * the reload animation's bone tracks (75 frames at 30 fps) in Unity space.

Coordinates: OAT's GLTF view-model space is right-handed with X forward, Y up, Z right.
Unity's staff space is left-handed with X right, Y up, Z forward (H3VR guns point down +Z).
Both name the same physical directions, so the conversion is the change of basis
(x, y, z) -> (z, y, x) for points and (x, y, z, w) -> (-z, -y, -x, w) for rotations.
Lengths are converted from inches to metres here; Unity needs no import scaling.
"""
import base64
import json
import math
import shutil
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

sys.path.insert(0, str(Path(__file__).parent))
from read_xanim import parse as parse_xanim, sample as xsample, QUAT_SCALE  # noqa: E402
import flac  # noqa: E402

INCH = 0.0254
MODEL_SCALE = 1.0
# BO2 exaggerates the first-person head (the cuff above the cage, the tip and the crystal); at VR
# scale it was bigger than the player's torso. Everything above HEAD_START (view-space inches,
# a clean gap in the mesh just below the cuff) is shrunk towards it, tapering in over HEAD_TAPER so
# the shaft and cuff meet without a step. The tip and crystal are scaled by the same amount and
# moved onto the shrunk head.
HEAD_SCALE = 0.6375  # 75%, then 15% smaller again after play-testing
HEAD_START = 16.0
HEAD_TAPER = 0.8
SCALE = INCH * MODEL_SCALE  # BO2 inches -> Unity metres, everywhere geometry is written
RELOAD_OFFSET_X = 8.409  # attachViewModelOffsetX6 in staff_<element>_zm
WORLD_TAIL_CUT_X = -10.5  # keep the world model only below this view-space X (view model starts at -11.25)

# BO2 effect textures (all additive-style, black background) used to rebuild the effects,
# which OAT cannot export. Flipbooks are columns x rows.
FX_TEXTURES = {
    "fxt_fire_flame_base_01": (4, 4),   # rolling fire puffs
    "fxt_fire_anim_flicker_1": (8, 2),  # licking flames
    "fxt_fire_flame_vert_c": (4, 1),    # flame jets
    "fxt_debris_fire_ember_cloud_01": (1, 1),  # soft ember glow
    "fxt_spark_field": (1, 1),
    "fxt_smoke_tile": (1, 1),
    "fxt_light_flare_cool": (1, 1),     # blue-white glow (ice, lightning)
    "fxt_fx_emp_ring_wave": (1, 1),     # shock ring (ice blast, lightning impact)
    "fxt_debris_clump_rocks": (2, 1),   # chips (ice shards, wind debris)
    "fxt_smk_def_0": (2, 1),            # billowing cloud (frost, dust)
    "fxt_electric_tile": (1, 1),        # crackling purple web
    "fxt_env_lighting_bolt_air": (2, 4),  # bolt flipbook
    "fxt_spark_pcloud_blue_1": (1, 1),
    "fxt_fx_ring_spiral": (1, 1),       # wind swirl
    "fxt_debris_heli_rotor_swirl": (2, 1),
    "fxt_light_flare": (1, 1),
    "fxt_env_lightning_bolt_trail2": (1, 2),  # two bolt strips: the charged ball's arcs
}
# BO2 shows an upgraded staff on the HUD by drawing a red glow behind its icon
# (zom_hud_craftable_staff_glow, STATE_GOT_UPGRADE in questitemtombdisplay.lua); it has no
# separate upgraded icon. The upgraded spawner icons are made the same way, in the glow's red.
UPGRADED_GLOW = (239, 28, 33)

# Static models the Unity builder uses for effects (Ripped/Props/<name>.json).
PROPS = {"IceShard": "p6_zm_tm_staff_projectile_ice"}  # BO2's icicle, 32 in long

ICONS = {
    "fire": "zom_hud_craftable_element_fire",
    "ice": "zom_hud_craftable_element_water",
    "lightning": "zom_hud_craftable_element_lightning",
    "wind": "zom_hud_craftable_element_wind",
}

# Sound aliases each element uses (from zmb_tomb.all.aliases.csv); the first alias with usable
# files wins, so later ones are fallbacks (BO2 never gave the wind staff its own pickup or raise
# sounds, nor lightning and wind a dry fire). "fire" is the shot, layered with its rear and flux
# secondaries the way BO2 chains them.
COMMON_SOUNDS = {
    "reload_1": ["wpn_staff_reload_1"],
    "reload_2": ["wpn_staff_reload_2"],
    "reload_3": ["wpn_staff_reload_3"],
    "reload_4": ["wpn_staff_reload_4"],
    "crystal": ["evt_crystal"],
}


def staff_sounds(bo2, **extra):
    s = {
        "fire": [f"wpn_{bo2}staff_fire_plr"],
        "fire_rear": [f"wpn_{bo2}staff_fire_rear"],
        "flux": [f"wpn_{bo2}staff_flux_left"],
        "proj_loop": [f"wpn_{bo2}staff_proj_loop"],
        "impact": [f"wpn_{bo2}staff_proj_impact"],
        "dryfire": [f"wpn_{bo2}staff_dryfire_plr", "wpn_firestaff_dryfire_plr"],
        "pickup": [f"wpn_{bo2}staff_pickup_plr", "wpn_firestaff_pickup_plr"],
        "handling": [f"wpn_{bo2}staff_raise_plr", "wpn_firestaff_raise_plr"],
        # The upgraded staffs' charge shot: a cue as each level is reached, and a loop while held.
        "charge_1": [f"wpn_{bo2}staff_charge_1"],
        "charge_2": [f"wpn_{bo2}staff_charge_2"],
        "charge_3": [f"wpn_{bo2}staff_charge_3"],
        "charge_loop": [f"wpn_{bo2}staff_charge_loop"],
    }
    s.update(COMMON_SOUNDS)
    s.update(extra)
    return s


SOUND_ALIASES = {
    "fire": staff_sounds("fire", impact_upgraded=["wpn_firestaff_proj_impact_upgraded"],
                         grenade_loop=["wpn_firestaff_grenade_loop"]),
    # The ice blast has no projectile: its "impact" is the freeze-ray hit on each zombie.
    "ice": staff_sounds("water", impact=["wpn_waterstaff_impact"], freeze=["wpn_waterstaff_freeze_zombie"],
                        shatter=["wpn_waterstaff_collapse_zombie"],
                        storm=["wpn_waterstaff_storm"], storm_imp=["wpn_waterstaff_storm_imp"]),
    "lightning": staff_sounds("lightning", zap=["wpn_lightningstaff_zmb_fx"],
                              impact_flash=["wpn_lightningstaff_proj_impact_flash"],
                              ball=["wpn_lightningstaff_ball"], ball_explo=["wpn_lightningstaff_ball_explo"],
                              arc=["wpn_imp_lightningstaff"]),
    "wind": staff_sounds("air", tornado=["wpn_airstaff_tornado"], tornado_imp=["wpn_airstaff_tornado_imp"]),
}


def variants(element, bo2, crystal, upg_anim):
    """Base and upgraded staff for an element, attached as staff_<bo2>_zm and
    staff_<bo2>_upgraded_zm do. Every base staff reuses the fire staff's reload animation
    (BO2 shares one reload skeleton), and every upgraded one its own."""
    title = element.capitalize()
    return [
        dict(name=f"Staff_{title}", tip=f"t6_wpn_zmb_staff_tip_{bo2}_view", crystal=f"t6_wpn_zmb_staff_crystal_{crystal}_view",
             reload=f"t6_wpn_zmb_staff_reload_{bo2}_view", anim="viewmodel_zomb_staff_fire_reload",
             idle="viewmodel_zomb_staff_idle", weapon=f"staff_{bo2}_zm"),
        dict(name=f"Staff_{title}_Upgraded", tip=f"t6_wpn_zmb_staff_tip_{bo2}_view", crystal=f"t6_wpn_zmb_upg_crystal_{crystal}_view",
             reload=f"t6_wpn_zmb_staff_reload_upg_{bo2}_view", anim=upg_anim,
             idle="viewmodel_z_staff_upg_idle", weapon=f"staff_{bo2}_upgraded_zm"),
    ]


VARIANTS = {
    "fire": variants("fire", "fire", "fire", "viewmodel_z_staff_upg_reload"),
    "ice": variants("ice", "water", "water", "viewmodel_z_staff_water_upg_reload"),
    "lightning": variants("lightning", "lightning", "bolt", "viewmodel_z_staff_lightning_upg_reload"),
    "wind": variants("wind", "air", "air", "viewmodel_z_staff_air_upg_reload"),
}


# ----------------------------------------------------------------------------- math

def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def qconj(q):
    return (-q[0], -q[1], -q[2], q[3])


def qrot(q, v):
    x, y, z, _ = qmul(qmul(q, (v[0], v[1], v[2], 0.0)), qconj(q))
    return (x, y, z)


def qnorm(q):
    n = math.sqrt(sum(c * c for c in q)) or 1.0
    return tuple(c / n for c in q)


def to_unity_pos(v, scale=SCALE):
    return [v[2] * scale, v[1] * scale, v[0] * scale]


def to_unity_rot(q):
    return [-q[2], -q[1], -q[0], q[3]]


def b64(arr, fmt):
    return base64.b64encode(np.asarray(arr, dtype=fmt).tobytes()).decode("ascii")


# ----------------------------------------------------------------------------- gltf

class Gltf:
    def __init__(self, path):
        self.path = Path(path)
        self.d = json.loads(self.path.read_text())
        self.buf = base64.b64decode(self.d["buffers"][0]["uri"].split(",", 1)[1])
        self.nodes = self.d["nodes"]
        self.parent = {c: i for i, n in enumerate(self.nodes) for c in n.get("children", [])}
        self.by_name = {n.get("name"): i for i, n in enumerate(self.nodes)}

    def acc(self, i):
        a = self.d["accessors"][i]
        bv = self.d["bufferViews"][a["bufferView"]]
        comp = {5126: "f", 5121: "B", 5123: "H", 5125: "I"}[a["componentType"]]
        n = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[a["type"]]
        off = bv.get("byteOffset", 0) + a.get("byteOffset", 0)
        stride = bv.get("byteStride", struct.calcsize(comp) * n)
        if stride == struct.calcsize(comp) * n:
            out = np.frombuffer(self.buf, dtype={"f": "<f4", "B": "u1", "H": "<u2", "I": "<u4"}[comp],
                                count=a["count"] * n, offset=off)
            return out.reshape(a["count"], n) if n > 1 else out
        rows = [struct.unpack_from("<" + comp * n, self.buf, off + k * stride) for k in range(a["count"])]
        return np.array(rows)

    def local(self, i):
        n = self.nodes[i]
        return tuple(n.get("translation", (0.0, 0.0, 0.0))), tuple(n.get("rotation", (0.0, 0.0, 0.0, 1.0)))

    def world(self, i):
        t, r = self.local(i)
        if i not in self.parent:
            return t, r
        pt, pr = self.world(self.parent[i])
        rt = qrot(pr, t)
        return (pt[0] + rt[0], pt[1] + rt[1], pt[2] + rt[2]), qmul(pr, r)

    def root_bone(self, i):
        while i in self.parent and not self.nodes[self.parent[i]].get("name", "").endswith("_skel"):
            i = self.parent[i]
        return i

    def tag(self, name):
        """Model-space frame for attaching another model at a tag (OAT's root axis turn removed)."""
        i = self.by_name[name]
        t, r = self.world(i)
        return t, qmul(r, qconj(self.local(self.root_bone(i))[1]))

    def images(self):
        return [Path(img["uri"]).name for img in self.d.get("images", [])]

    def material(self, mi):
        m = self.d["materials"][mi]
        images, textures = self.images(), self.d.get("textures", [])

        def tex(slot):
            return images[textures[slot["index"]]["source"]] if slot else ""

        albedo = tex(m.get("pbrMetallicRoughness", {}).get("baseColorTexture"))
        name = m["name"].rsplit("/", 1)[-1]
        glow = "crystal" in name or "pwr_srce" in name
        return {"name": name, "albedo": albedo, "normal": tex(m.get("normalTexture")), "emissive": albedo if glow else ""}

    def primitives(self):
        for mesh in self.d["meshes"]:
            for p in mesh["primitives"]:
                a = p["attributes"]
                yield {
                    "pos": self.acc(a["POSITION"]).astype(np.float64),
                    "nrm": self.acc(a["NORMAL"]).astype(np.float64),
                    "uv": self.acc(a["TEXCOORD_0"]).astype(np.float64),
                    "idx": self.acc(p["indices"]).astype(np.int64).reshape(-1, 3),
                    "joints": self.acc(a["JOINTS_0"]).astype(np.int64) if "JOINTS_0" in a else None,
                    "weights": self.acc(a["WEIGHTS_0"]).astype(np.float64) if "WEIGHTS_0" in a else None,
                    "material": p.get("material", 0),
                }


# ----------------------------------------------------------------------------- assembly

class Assembly:
    """Transforms + meshes in Unity staff space, serialised for StaffAssemblyBuilder.cs."""

    def __init__(self, name, tex_src, tex_dst):
        self.name = name
        self.bones = []  # {name, parent, pos, rot}
        self.meshes = []
        self.tracks = []
        self.points = {}
        self.tex_src, self.tex_dst = tex_src, tex_dst

    def add_bone(self, name, parent, pos_v, rot_v, scale=1.0):
        bone = {"name": name, "parent": parent, "pos": to_unity_pos(pos_v), "rot": to_unity_rot(rot_v)}
        if scale != 1.0:
            bone["scale"] = scale
        self.bones.append(bone)
        return len(self.bones) - 1

    def add_skeleton(self, gltf, parent, prefix=""):
        """Add every node under the model's *_skel node, keeping local transforms."""
        index = {}
        skel = next(i for i, n in enumerate(gltf.nodes) if n.get("name", "").endswith("_skel"))

        def walk(i, p):
            t, r = gltf.local(i)
            index[i] = self.add_bone(prefix + gltf.nodes[i]["name"], p, t, r)
            for c in gltf.nodes[i].get("children", []):
                if gltf.nodes[c].get("name", "").startswith(("tag_", "j_")):
                    walk(c, index[i])

        for c in gltf.nodes[skel].get("children", []):
            if gltf.nodes[c].get("name", "").startswith(("tag_", "j_")):
                walk(c, parent)
        return index

    def copy_texture(self, name):
        if name and not (self.tex_dst / name).exists():
            shutil.copy2(self.tex_src / name, self.tex_dst / name)

    def add_mesh(self, name, gltf, parent, transform=None, clip_x=None, skin=None):
        """Bake a model's primitives into one mesh (one submesh per material).

        transform: optional function mapping (n,3) model-space points/normals to staff view space.
        clip_x:    keep only geometry with view-space X below this value (triangles are split).
        skin:      dict node-index -> assembly bone index; vertices keep their bone weights.
        """
        verts, norms, uvs, joints, weights = [], [], [], [], []
        submeshes = {}
        base = 0
        for p in gltf.primitives():
            pos, nrm, uv = p["pos"], p["nrm"], p["uv"].copy()
            uv[:, 1] = 1.0 - uv[:, 1]  # GLTF V points down; Unity's points up
            if transform:
                pos, nrm = transform(pos, nrm)
            tris = p["idx"]
            jw = None
            if skin is not None and p["joints"] is not None:
                skin_nodes = gltf.d["skins"][0]["joints"]
                # Joints outside the tag_/j_ skeleton (e.g. the *_skel node) ride on the mesh's parent.
                lut = np.array([skin.get(n, parent) for n in skin_nodes])
                jw = (lut[p["joints"]], p["weights"])
            if clip_x is not None:
                pos, nrm, uv, tris, jw = clip_below(pos, nrm, uv, tris, clip_x, jw)
            if len(tris) == 0:
                continue
            mat = gltf.material(p["material"])
            for k in ("albedo", "normal"):
                self.copy_texture(mat[k])
            submeshes.setdefault(mat["name"], {"material": mat, "tris": []})["tris"].append(tris + base)
            verts.append(pos)
            norms.append(nrm)
            uvs.append(uv)
            if jw is not None:
                joints.append(jw[0])
                weights.append(jw[1])
            base += len(pos)
        if base == 0:
            return None
        pos = np.concatenate(verts)
        nrm = np.concatenate(norms)
        uv = np.concatenate(uvs)
        # Unity space: (x, y, z)view -> (z, y, x), inches -> metres (with the model scale).
        pos_u = pos[:, [2, 1, 0]] * SCALE
        nrm_u = nrm[:, [2, 1, 0]]
        nrm_u /= np.linalg.norm(nrm_u, axis=1, keepdims=True).clip(1e-9)
        subs = []
        for s in submeshes.values():
            tris = np.concatenate(s["tris"])
            tris = fix_winding(pos_u, nrm_u, tris)
            subs.append({"material": s["material"], "triangles": b64(tris.reshape(-1), "<i4")})
        mesh = {
            "name": name, "bone": parent, "vertexCount": int(len(pos_u)),
            "vertices": b64(pos_u.reshape(-1), "<f4"), "normals": b64(nrm_u.reshape(-1), "<f4"),
            "uv": b64(uv.reshape(-1), "<f4"), "submeshes": subs, "skinned": bool(joints),
        }
        if joints:
            mesh["boneIndices"] = b64(np.concatenate(joints).reshape(-1), "<i4")
            mesh["boneWeights"] = b64(np.concatenate(weights).reshape(-1), "<f4")
        self.meshes.append(mesh)
        return mesh

    def to_json(self, extra):
        out = {"name": self.name, "bones": self.bones, "meshes": self.meshes, "tracks": self.tracks,
               "points": [{"name": k, "pos": v} for k, v in self.points.items()]}
        out.update(extra)
        return json.dumps(out)


def fix_winding(pos, nrm, tris):
    """Unity draws clockwise triangles (seen from the front) in its left-handed space, where
    cross(b - a, c - a) on the raw coordinates points out of the front face. Flip the mesh if
    most triangles disagree with their own vertex normals."""
    a, b, c = pos[tris[:, 0]], pos[tris[:, 1]], pos[tris[:, 2]]
    face = np.cross(b - a, c - a)
    agree = np.einsum("ij,ij->i", face, nrm[tris[:, 0]] + nrm[tris[:, 1]] + nrm[tris[:, 2]])
    if (agree < 0).sum() > (agree > 0).sum():
        tris = tris[:, [0, 2, 1]]
    return tris


def clip_below(pos, nrm, uv, tris, cut, jw=None):
    """Keep the part of a mesh with x < cut, splitting triangles that cross the plane."""
    keep = pos[:, 0] < cut
    new_pos, new_nrm, new_uv = list(pos), list(nrm), list(uv)
    new_j = list(jw[0]) if jw else None
    new_w = list(jw[1]) if jw else None
    out = []
    cache = {}

    def mid(i, j):
        key = (min(i, j), max(i, j))
        if key not in cache:
            t = (cut - pos[i, 0]) / (pos[j, 0] - pos[i, 0])
            new_pos.append(pos[i] + (pos[j] - pos[i]) * t)
            n = nrm[i] + (nrm[j] - nrm[i]) * t
            new_nrm.append(n / (np.linalg.norm(n) or 1))
            new_uv.append(uv[i] + (uv[j] - uv[i]) * t)
            if jw:
                new_j.append(jw[0][i])
                new_w.append(jw[1][i])
            cache[key] = len(new_pos) - 1
        return cache[key]

    for tri in tris:
        inside = [v for v in tri if keep[v]]
        if len(inside) == 3:
            out.append(list(tri))
            continue
        if not inside:
            continue
        # Walk the triangle edges in order, emitting the clipped polygon (keeps winding).
        poly = []
        for k in range(3):
            i, j = tri[k], tri[(k + 1) % 3]
            if keep[i]:
                poly.append(i)
            if keep[i] != keep[j]:
                poly.append(mid(i, j))
        for k in range(1, len(poly) - 1):
            out.append([poly[0], poly[k], poly[k + 1]])
    used = sorted({v for t in out for v in t})
    remap = {v: n for n, v in enumerate(used)}
    tri_out = np.array([[remap[v] for v in t] for t in out], dtype=np.int64).reshape(-1, 3)
    P = np.array([new_pos[v] for v in used])
    N = np.array([new_nrm[v] for v in used])
    U = np.array([new_uv[v] for v in used])
    J = (np.array([new_j[v] for v in used]), np.array([new_w[v] for v in used])) if jw else None
    return P, N, U, tri_out, J


def world_to_view_transform(world, view):
    """Rigid map from world-model space onto the view model's axis (X forward, Y up, Z right)."""
    P = np.concatenate([p["pos"] for p in world.primitives()])
    c = P.mean(0)
    _, _, vt = np.linalg.svd(P - c)
    ex = vt[0] if vt[0][1] > 0 else -vt[0]  # the staff's long axis, pointing at the tip
    ez = np.array([0.0, 0.0, 1.0])
    ez = ez - ex * ez.dot(ex)
    ez /= np.linalg.norm(ez)
    ey = np.cross(ez, ex)
    R = np.stack([ex, ey, ez])  # rows: view X, Y, Z expressed in world space
    # Slide along the axis so both models' tips coincide.
    world_tip = np.array(world.tag("tag_tip")[0])
    view_tip_x = view.tag("tag_tip")[0][0]
    shift = view_tip_x - (world_tip - c).dot(ex)

    def transform(pos, nrm):
        p = (pos - c) @ R.T
        p[:, 0] += shift
        return p, nrm @ R.T

    return transform


def head_x(x):
    """Where a view-space X ends up after the head is shrunk."""
    return x if x <= HEAD_START else HEAD_START + (x - HEAD_START) * HEAD_SCALE


def head_warp(offset_x=0.0):
    """Mesh transform shrinking everything above HEAD_START; offset_x converts the mesh's own X
    to view space (the reload assembly sits RELOAD_OFFSET_X up the staff)."""
    def transform(pos, nrm):
        pos = pos.copy()
        x = pos[:, 0] + offset_x
        radial = np.interp(x, [HEAD_START, HEAD_START + HEAD_TAPER], [1.0, HEAD_SCALE])
        pos[:, 0] = np.where(x > HEAD_START, HEAD_START + (x - HEAD_START) * HEAD_SCALE, x) - offset_x
        pos[:, 1] *= radial
        pos[:, 2] *= radial
        return pos, nrm
    return transform


def cyan_centre(gltf, tex_dir):
    """View-space X of the glowing blue rings on the staff (sampled from the colour texture)."""
    xs = []
    for p in gltf.primitives():
        mat = gltf.material(p["material"])
        if not mat["albedo"]:
            continue
        im = Image.open(tex_dir / mat["albedo"]).convert("RGB")
        w, h = im.size
        px = np.asarray(im)
        u = (p["uv"][:, 0] % 1.0) * (w - 1)
        v = (p["uv"][:, 1] % 1.0) * (h - 1)  # GLTF V already points down, like image rows
        rgb = px[v.astype(int), u.astype(int)]
        cyan = (rgb[:, 2] > 120) & (rgb[:, 1] > 100) & (rgb[:, 0] < 90)
        xs.extend(p["pos"][cyan, 0])
    return float(np.median(xs)), float(np.min(xs)), float(np.max(xs))


def reload_tracks(asm, anim_path, bone_index, staff_bones):
    """Per-frame local transforms for every animated bone we have, in Unity space.

    BO2 stores these bones' translations as offsets from the bind pose and rotations as
    absolute local rotations (checked against the bind pose: frame 0 matches exactly)."""
    a = parse_xanim(anim_path)
    frames = a["frames"]
    for name, (q, t) in a["tracks"].items():
        bi = bone_index.get(name, staff_bones.get(name))
        if bi is None:
            continue
        moving_t = t and len(t) > 1 and any(abs(c) > 1e-4 for _, v in t for c in v)
        moving_q = q and len(q) > 1
        if not (moving_t or moving_q):
            continue
        track = {"bone": bi}
        if moving_t:
            pos = [to_unity_pos(xsample(t, f)) for f in range(frames + 1)]
            track["posOffset"] = b64(np.array(pos).reshape(-1), "<f4")
        if moving_q:
            rots = []
            prev = None
            for f in range(frames + 1):
                r = to_unity_rot(qnorm([c * QUAT_SCALE for c in xsample(q, f)]))
                if prev is not None and sum(x * y for x, y in zip(prev, r)) < 0:
                    r = [-c for c in r]  # keep neighbouring keys on the same hemisphere
                rots.append(r)
                prev = r
            track["rot"] = b64(np.array(rots).reshape(-1), "<f4")
        asm.tracks.append(track)
    notes = [{"name": n.split("#", 1)[-1], "frame": f} for n, f in a["notes"] if n.startswith("sndnt#")]
    return frames, a["fps"], notes


def export_fx(img_dump, out):
    """Convert the BO2 effect textures and spawner icons to PNG under Ripped/FX."""
    dst = out / "FX"
    dst.mkdir(parents=True, exist_ok=True)
    fx = []
    for name, (cols, rows) in FX_TEXTURES.items():
        png = dst / f"{name}.png"
        if not png.exists():
            with Image.open(img_dump / "images" / f"{name}.dds") as im:
                im.load()
                im.save(png)
        fx.append({"name": name, "file": f"{name}.png", "columns": cols, "rows": rows})
    for name in ICONS.values():
        png = dst / f"{name}.png"
        if not png.exists():
            with Image.open(img_dump / "images" / f"{name}.dds") as im:
                im.load()
                im.save(png)
        upgraded_icon(png, dst / f"{name}_upgraded.png")
    return fx


def upgraded_icon(src, dst):
    """The base icon at twice the size with BO2's red upgrade glow around it."""
    with Image.open(src) as im:
        icon = im.convert("RGBA")
    icon = icon.resize((icon.width * 2, icon.height * 2), Image.LANCZOS)
    alpha = icon.split()[3]
    glow = alpha.filter(ImageFilter.MaxFilter(5)).filter(ImageFilter.GaussianBlur(1.5))
    glow = glow.point(lambda v: min(255, int(v * 1.3)))
    red = Image.new("RGBA", icon.size, UPGRADED_GLOW + (0,))
    red.putalpha(glow)
    Image.alpha_composite(red, icon).save(dst)


def export_props(extracted, out):
    for name, model in PROPS.items():
        asm = Assembly(name, extracted / "images", out / "Textures")
        root = asm.add_bone("Root", -1, (0, 0, 0), (0, 0, 0, 1))
        asm.add_mesh(name, Gltf(extracted / "models" / f"{model}_png.gltf"), root)
        path = out / "Props" / f"{name}.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(asm.to_json({}))
        print(f"{name}: {sum(m['vertexCount'] for m in asm.meshes)} verts -> {path}")


def export_sounds(dump, out, element):
    """Copy each alias's WAV files to Ripped/Sounds/<element> and return their BO2 volume/pitch
    ranges (file names are relative to Ripped/Sounds).

    BO2 alias volumes are 0-100 and pitches are in cents; Unity gets 0-1 and playback rates."""
    import csv
    rows = list(csv.reader((dump / "soundbank" / "zmb_tomb.all.aliases.csv").open(encoding="utf-8", errors="replace")))
    head = rows[0]
    col = {k: head.index(k) for k in ("Name", "FileSource", "VolMin", "VolMax", "PitchMin", "PitchMax")}
    by_alias = {}
    for r in rows[1:]:
        if len(r) > col["PitchMax"]:
            by_alias.setdefault(r[col["Name"]], []).append(r)
    dst = out / "Sounds" / element
    dst.mkdir(parents=True, exist_ok=True)
    sounds = []
    for key, aliases in SOUND_ALIASES[element].items():
        for alias in aliases:
            entries = by_alias.get(alias, [])
            files = []
            for r in entries:
                rel = r[col["FileSource"]].replace("\\", "/").split("raw/sound/", 1)[-1]
                src = dump / "sound" / rel
                if src.suffix.lower() not in (".wav", ".flac") or not src.exists():
                    continue
                name = src.name.split(".")[0] + ".wav"
                if src.suffix.lower() == ".flac":
                    if not (dst / name).exists():
                        flac.to_wav(src, dst / name)  # Unity 5.6 cannot import FLAC
                else:
                    shutil.copy2(src, dst / name)
                files.append(f"{element}/{name}")
            if not files:
                continue
            r = entries[0]
            vol = [float(r[col["VolMin"]] or 100) / 100.0, float(r[col["VolMax"]] or 100) / 100.0]
            pitch = [2 ** (float(r[col["PitchMin"]] or 0) / 1200.0), 2 ** (float(r[col["PitchMax"]] or 0) / 1200.0)]
            sounds.append({"key": key, "alias": alias, "files": sorted(set(files)), "volume": vol, "pitch": pitch})
            break
        else:
            print(f"  ! {element}: no sound for {key}")
    return sounds


def apply_idle_pose(asm, anim_path, bone_index):
    """Record each variant's BO2 idle pose as a rest pose on top of the bind pose.

    Skinning needs the bind pose (the mesh is authored there), so the pose is stored separately
    as restPos/restRot and applied by the Unity builder after it computes bind matrices. This is
    what distinguishes the upgraded staff: its idle flips the tip knives (tag_tip_fire5/6)
    outward by 116 degrees and leaves the five head studs (tag_upg_*_1) extended, where the base
    idle tucks them 1.38 in into the shaft."""
    a = parse_xanim(anim_path)
    posed = 0
    for name, (q, t) in a["tracks"].items():
        bi = bone_index.get(name)
        if bi is None:
            continue
        bone = asm.bones[bi]
        if t:
            offset = to_unity_pos(xsample(t, 0))
            bone["restPos"] = [p + o for p, o in zip(bone["pos"], offset)]
        if q:
            bone["restRot"] = to_unity_rot(qnorm([c * QUAT_SCALE for c in xsample(q, 0)]))
        if t or q:
            posed += 1
    return posed


def weapon_stats(dump, weapon):
    parts = (dump / "weapons" / weapon).read_text(encoding="utf-8", errors="replace").split("\\")
    d = dict(zip(parts[1::2], parts[2::2]))
    keys = ("clipSize", "maxAmmo", "fireTime", "projectileSpeed", "explosionRadius",
            "explosionInnerDamage", "explosionOuterDamage", "projectileLifetime", "damage")
    return {k: float(d[k]) for k in keys if d.get(k)}


def build_variant(v, element, dump, extracted, out, fx, sounds):
    models = extracted / "models"
    tex_src, tex_dst = extracted / "images", out / "Textures"
    tex_dst.mkdir(parents=True, exist_ok=True)
    asm = Assembly(v["name"], tex_src, tex_dst)

    view = Gltf(models / "t6_wpn_zmb_staff_view_png.gltf")
    world = Gltf(models / "t6_wpn_zmb_staff_world_png.gltf")

    root = asm.add_bone("StaffRoot", -1, (0, 0, 0), (0, 0, 0, 1))
    staff_bones = asm.add_skeleton(view, root)
    staff_by_name = {view.nodes[i]["name"]: b for i, b in staff_bones.items()}
    asm.add_mesh("StaffBody", view, root, transform=head_warp(), skin=staff_bones)

    tail_xf = world_to_view_transform(world, view)
    asm.add_mesh("StaffTail", world, root, transform=tail_xf, clip_x=WORLD_TAIL_CUT_X)

    # The tip keeps its bones: BO2 poses its knives differently on the upgraded staff.
    t, r = view.tag("tag_tip")
    tip_mount = asm.add_bone("Mount_tip", root, (head_x(t[0]), t[1], t[2]), r, scale=HEAD_SCALE)
    tip = Gltf(models / f"{v['tip']}_png.gltf")
    tip_bones = asm.add_skeleton(tip, tip_mount)
    tip_by_name = {tip.nodes[i]["name"]: b for i, b in tip_bones.items()}
    asm.add_mesh(v["tip"].replace("t6_wpn_zmb_", ""), tip, tip_mount, skin=tip_bones)

    t, r = view.tag("tag_crystal")
    crystal_mount = asm.add_bone("Mount_crystal", root, (head_x(t[0]), t[1] * HEAD_SCALE, t[2] * HEAD_SCALE), r, scale=HEAD_SCALE)
    asm.add_mesh(v["crystal"].replace("t6_wpn_zmb_", ""), Gltf(models / f"{v['crystal']}_png.gltf"), crystal_mount)

    reload = Gltf(models / f"{v['reload']}_png.gltf")
    mount = asm.add_bone("Mount_reload", root, (RELOAD_OFFSET_X, 0, 0), (0, 0, 0, 1))
    reload_bones = asm.add_skeleton(reload, mount)
    reload_by_name = {reload.nodes[i]["name"]: b for i, b in reload_bones.items()}
    asm.add_mesh("ReloadAssembly", reload, mount, transform=head_warp(RELOAD_OFFSET_X), skin=reload_bones)

    frames, fps, notes = reload_tracks(asm, dump / "xanim" / v["anim"], reload_by_name, staff_by_name)
    posed = apply_idle_pose(asm, dump / "xanim" / v["idle"], dict(staff_by_name, **tip_by_name))

    # Points the Unity builder places grips, muzzle and colliders on.
    centre, lo, hi = cyan_centre(view, extracted / "images")
    asm.points["blueRings"] = to_unity_pos((centre, 0, 0))
    asm.points["blueRingsStart"] = to_unity_pos((lo, 0, 0))
    asm.points["blueRingsEnd"] = to_unity_pos((hi, 0, 0))
    flash = np.array(view.tag("tag_flash")[0])
    asm.points["muzzle"] = to_unity_pos((head_x(flash[0]), flash[1] * HEAD_SCALE, flash[2] * HEAD_SCALE))
    tail_pts = np.concatenate([tail_xf(p["pos"], p["nrm"])[0] for p in world.primitives()])
    tail_min = float(tail_pts[:, 0].min())
    asm.points["buttEnd"] = to_unity_pos((tail_min, 0, 0))
    # The support hand holds the black wrapped handle, roughly halfway between the blue rings and
    # the brass fitting 5 in above the butt (located by texture brightness), nudged 2.4 in up after
    # play-testing put the hand just below the wrap.
    asm.points["supportGrip"] = to_unity_pos(((tail_min + 5.0 + centre) * 0.5 + 2.4, 0, 0))
    asm.points["crystalSource"] = to_unity_pos((RELOAD_OFFSET_X + 7.3, 0, 0))

    extra = {"frames": frames, "fps": fps, "notes": notes, "stats": weapon_stats(dump, v["weapon"]),
             "element": element, "upgraded": v["name"].endswith("_Upgraded"),
             "icon": ICONS[element] + ("_upgraded.png" if v["name"].endswith("_Upgraded") else ".png"),
             "fx": fx, "sounds": sounds, "modelScale": MODEL_SCALE}
    path = out / "Assembly" / f"{v['name']}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(asm.to_json(extra))
    print(f"{v['name']}: {len(asm.bones)} transforms, {len(asm.meshes)} meshes "
          f"({sum(m['vertexCount'] for m in asm.meshes)} verts), {len(asm.tracks)} animated bones, "
          f"{posed} bones posed by {v['idle']}, blue rings x {lo:.1f}..{hi:.1f} in, butt at x {tail_min:.1f} in -> {path}")


def main(dump, img_dump, extracted, out, elements):
    dump, img_dump, extracted, out = Path(dump), Path(img_dump), Path(extracted), Path(out)
    fx = export_fx(img_dump, out)
    export_props(extracted, out)
    if elements == ["all"]:
        elements = list(VARIANTS)
    for el in elements:
        sounds = export_sounds(dump, out, el)
        for v in VARIANTS[el]:
            build_variant(v, el, dump, extracted, out, fx, sounds)


if __name__ == "__main__":
    if len(sys.argv) < 6:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5:])

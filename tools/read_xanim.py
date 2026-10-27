"""Decode a BO2 (T6, version 19) compiled xanim as dumped by OpenAssetTools.

Usage:
    python read_xanim.py <xanim_file> [bone_substring ...]

Prints the header, every bone track that actually moves, and the per-frame local
translation (inches) and rotation (degrees, as Euler XYZ) of the requested bones.
Format follows OAT's src/ObjLoading/XAnim/CompiledXAnimLoader.cpp.
"""
import math
import struct
import sys

QUAT_SCALE = 1.0 / 32767.0  # CoD packs quaternion components as int16 / 0x7FFF
HALF_TRANS_SIZE_SCALE = 0.003921568859368563
FULL_TRANS_SIZE_SCALE = 0.00001525902189314365


class Reader:
    def __init__(self, data):
        self.d, self.p = data, 0

    def take(self, fmt):
        v = struct.unpack_from("<" + fmt, self.d, self.p)
        self.p += struct.calcsize("<" + fmt)
        return v if len(v) > 1 else v[0]

    def cstr(self):
        end = self.d.index(b"\0", self.p)
        s = self.d[self.p:end].decode("ascii", "replace")
        self.p = end + 1
        return s


def full_quat(r):
    x, y, z = r.take("3h")
    w2 = 0x3FFF0001 - (x * x + y * y + z * z)
    w = int(math.floor(math.sqrt(w2) + 0.5)) if w2 > 0 else 0
    return [x, y, z, w]


def half_quat(r):
    z = r.take("h")
    w2 = 0x3FFF0001 - z * z
    w = int(math.floor(math.sqrt(w2) + 0.5)) if w2 > 0 else 0
    return [0, 0, z, w]


def indices(r, n, loop_frames, byte_idx):
    if n >= loop_frames:
        return list(range(n))
    return list(r.take(f"{n}B" if byte_idx else f"{n}H")) if n > 1 else [r.take("B" if byte_idx else "H")]


def read_quat_track(r, loop_frames, byte_idx, flip, half):
    n = r.take("H")
    if n == 0:
        return None
    if n == 1:
        q = half_quat(r) if half else full_quat(r)
        return [(0, [-v for v in q] if flip else q)]
    idx = indices(r, n, loop_frames, byte_idx)
    frames = []
    for i in range(n):
        q = half_quat(r) if half else full_quat(r)
        if i > 0 and sum(a * b for a, b in zip(frames[-1], q)) < 0:
            q = [-v for v in q]
        elif i == 0 and flip:
            q = [-v for v in q]
        frames.append(q)
    return list(zip(idx, frames))


def read_trans_track(r, loop_frames, byte_idx):
    n = r.take("H")
    if n == 0:
        return None
    if n == 1:
        return [(0, list(r.take("3f")))]
    idx = indices(r, n, loop_frames, byte_idx)
    small = r.take("?")
    mins = r.take("3f")
    size = [s * (HALF_TRANS_SIZE_SCALE if small else FULL_TRANS_SIZE_SCALE) for s in r.take("3f")]
    raw = r.take(f"{n * 3}{'B' if small else 'H'}")
    frames = [[mins[k] + size[k] * raw[i * 3 + k] for k in range(3)] for i in range(n)]
    return list(zip(idx, frames))


def parse(path):
    r = Reader(open(path, "rb").read())
    version, num_frames, bone_count, flags, asset_type, framerate = r.take("HHHBBH")
    assert version == 19, f"expected version 19, got {version}"
    looped = bool(flags & 1)
    loop_frames = num_frames + 1 if looped else num_frames
    byte_idx = (loop_frames - 1) < 256
    if flags & 0x2:
        raise SystemExit("delta tracks not supported (viewmodel anims have none)")
    mask = (bone_count + 7) // 8
    flip_bits, half_bits = r.take(f"{mask}B"), r.take(f"{mask}B")
    if mask == 1:
        flip_bits, half_bits = [flip_bits], [half_bits]
    names = [r.cstr() for _ in range(bone_count)]
    tracks = {}
    for i, name in enumerate(names):
        flip = bool(flip_bits[i // 8] & (1 << (i % 8)))
        half = bool(half_bits[i // 8] & (1 << (i % 8)))
        q = read_quat_track(r, loop_frames, byte_idx, flip, half)
        t = read_trans_track(r, loop_frames, byte_idx)
        tracks[name] = (q, t)
    notes = []
    for _ in range(r.take("B")):
        notes.append((r.cstr(), r.take("H")))
    return {"frames": num_frames, "fps": framerate, "looped": looped, "tracks": tracks, "notes": notes,
            "trailing": len(r.d) - r.p}


def sample(track, frame):
    """Linear interpolation between keyframes (quats normalised lerp)."""
    if track is None:
        return None
    if len(track) == 1:
        return track[0][1]
    for (f0, v0), (f1, v1) in zip(track, track[1:]):
        if f0 <= frame <= f1:
            a = 0 if f1 == f0 else (frame - f0) / (f1 - f0)
            return [x + (y - x) * a for x, y in zip(v0, v1)]
    return track[-1][1]


def euler(q):
    x, y, z, w = [v * QUAT_SCALE for v in q]
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1
    x, y, z, w = x / n, y / n, z / n, w / n
    roll = math.degrees(math.atan2(2 * (w * x + y * z), 1 - 2 * (x * x + y * y)))
    pitch = math.degrees(math.asin(max(-1, min(1, 2 * (w * y - z * x)))))
    yaw = math.degrees(math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)))
    return [round(roll, 1), round(pitch, 1), round(yaw, 1)]


def moves(track):
    if not track or len(track) < 2:
        return False
    vals = [v for _, v in track]
    return any(abs(a - b) > 1e-3 * (1 if isinstance(a, float) else 100) for v in vals for a, b in zip(v, vals[0]))


def main(path, wanted):
    a = parse(path)
    print(f"{path}: {a['frames']} frames @ {a['fps']} fps ({a['frames'] / a['fps']:.2f}s), looped={a['looped']}, "
          f"{len(a['tracks'])} bones, trailing bytes={a['trailing']}")
    print("notetracks:", [(n, f) for n, f in a["notes"]])
    moving = [n for n, (q, t) in a["tracks"].items() if moves(q) or moves(t)]
    print("moving bones:", ", ".join(moving))
    for name, (q, t) in a["tracks"].items():
        if not any(w in name for w in wanted):
            continue
        print(f"\n== {name}")
        step = max(1, a["frames"] // 25)
        for f in list(range(0, a["frames"] + 1, step)) + ([a["frames"]] if a["frames"] % step else []):
            tv, qv = sample(t, f), sample(q, f)
            print(f"  f{f:3d} t={[round(v, 2) for v in tv] if tv else '-'} r={euler(qv) if qv else '-'}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2:])

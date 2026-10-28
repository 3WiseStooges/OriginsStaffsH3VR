"""Minimal FLAC decoder, so BO2's streamed loops (the ice blizzard, the wind tornado) can be
converted to WAV: Unity 5.6 cannot import FLAC, and nothing else on the toolchain decodes it.

Implements the parts of FLAC that encoders actually emit: STREAMINFO, every subframe type
(constant, verbatim, fixed, LPC, wasted bits), Rice and Rice2 residuals with escapes, and all
stereo decorrelation modes. Frame CRCs are skipped.

Usage: python flac.py <in.flac> <out.wav>
"""
import sys
import wave

import numpy as np


class Bits:
    def __init__(self, data, byte_pos=0):
        self.data = data
        self.pos = byte_pos * 8
        self.end = len(data) * 8

    def read(self, n):
        if n == 0:
            return 0
        start, off = self.pos >> 3, self.pos & 7
        nbytes = (off + n + 7) >> 3
        chunk = int.from_bytes(self.data[start:start + nbytes], "big")
        self.pos += n
        return (chunk >> (nbytes * 8 - off - n)) & ((1 << n) - 1)

    def signed(self, n):
        v = self.read(n)
        return v - (1 << n) if n and v >> (n - 1) else v

    def unary(self):
        """Zeros before the next one bit (the one is consumed)."""
        count = 0
        while True:
            off = self.pos & 7
            b = (self.data[self.pos >> 3] << off) & 0xFF
            if b:
                lead = 8 - b.bit_length()
                self.pos += lead + 1
                return count + lead
            count += 8 - off
            self.pos += 8 - off

    def align(self):
        self.pos = (self.pos + 7) & ~7


def _residual(bits, block, order):
    method = bits.read(2)
    pbits, escape = (4, 15) if method == 0 else (5, 31)
    porder = bits.read(4)
    out = []
    for p in range(1 << porder):
        n = (block >> porder) - (order if p == 0 else 0)
        param = bits.read(pbits)
        if param == escape:
            raw = bits.read(5)
            out.extend(bits.signed(raw) for _ in range(n))
            continue
        read, unary = bits.read, bits.unary
        for _ in range(n):
            v = (unary() << param) | read(param)
            out.append((v >> 1) ^ -(v & 1))
    return out


def _subframe(bits, block, size):
    bits.read(1)
    kind = bits.read(6)
    wasted = bits.unary() + 1 if bits.read(1) else 0
    size -= wasted
    if kind == 0:
        s = [bits.signed(size)] * block
    elif kind == 1:
        s = [bits.signed(size) for _ in range(block)]
    elif 8 <= kind <= 12:
        order = kind - 8
        s = [bits.signed(size) for _ in range(order)]
        for r in _residual(bits, block, order):
            if order == 0:
                p = 0
            elif order == 1:
                p = s[-1]
            elif order == 2:
                p = 2 * s[-1] - s[-2]
            elif order == 3:
                p = 3 * s[-1] - 3 * s[-2] + s[-3]
            else:
                p = 4 * s[-1] - 6 * s[-2] + 4 * s[-3] - s[-4]
            s.append(p + r)
    elif kind >= 32:
        order = kind - 31
        s = [bits.signed(size) for _ in range(order)]
        precision = bits.read(4) + 1
        shift = bits.signed(5)
        coefs = [bits.signed(precision) for _ in range(order)]
        rev = coefs[::-1]
        for r in _residual(bits, block, order):
            window = s[-order:]
            s.append((sum(c * x for c, x in zip(rev, window)) >> shift) + r)
    else:
        raise ValueError(f"reserved subframe type {kind}")
    if wasted:
        s = [x << wasted for x in s]
    return s


def decode(path):
    """Returns (sample_rate, bits_per_sample, samples[n, channels] as int32)."""
    data = open(path, "rb").read()
    if data[:4] != b"fLaC":
        raise ValueError(f"{path} is not FLAC")
    pos = 4
    rate = channels = bps = total = 0
    while True:
        head = data[pos]
        length = int.from_bytes(data[pos + 1:pos + 4], "big")
        if head & 0x7F == 0:
            b = Bits(data[pos + 4:pos + 4 + length])
            b.read(16), b.read(16), b.read(24), b.read(24)
            rate, channels, bps, total = b.read(20), b.read(3) + 1, b.read(5) + 1, b.read(36)
        pos += 4 + length
        if head & 0x80:
            break

    bits = Bits(data, pos)
    frames = []
    while bits.end - bits.pos >= 64:
        if bits.read(14) != 0x3FFE:
            raise ValueError(f"lost frame sync at byte {bits.pos // 8}")
        bits.read(2)
        bs_code, sr_code = bits.read(4), bits.read(4)
        assign, ss_code = bits.read(4), bits.read(3)
        bits.read(1)
        first = bits.read(8)
        ones = 0
        while first & (0x80 >> ones):
            ones += 1
        for _ in range(max(ones - 1, 0)):
            bits.read(8)
        if bs_code == 1:
            block = 192
        elif 2 <= bs_code <= 5:
            block = 576 << (bs_code - 2)
        elif bs_code == 6:
            block = bits.read(8) + 1
        elif bs_code == 7:
            block = bits.read(16) + 1
        else:
            block = 256 << (bs_code - 8)
        if sr_code == 12:
            bits.read(8)
        elif sr_code in (13, 14):
            bits.read(16)
        bits.read(8)  # CRC-8
        size = {0: bps, 1: 8, 2: 12, 4: 16, 5: 20, 6: 24, 7: 32}[ss_code]

        n_ch = assign + 1 if assign < 8 else 2
        side = {8: 1, 9: 0, 10: 1}.get(assign, -1)
        sub = [np.array(_subframe(bits, block, size + (1 if c == side else 0)), dtype=np.int64) for c in range(n_ch)]
        if assign == 8:
            sub[1] = sub[0] - sub[1]
        elif assign == 9:
            sub[0] = sub[0] + sub[1]
        elif assign == 10:
            mid = (sub[0] << 1) | (sub[1] & 1)
            sub = [(mid + sub[1]) >> 1, (mid - sub[1]) >> 1]
        frames.append(np.stack(sub, axis=1))
        bits.align()
        bits.read(16)  # CRC-16
    samples = np.concatenate(frames).astype(np.int32)
    if total and len(samples) != total:
        raise ValueError(f"decoded {len(samples)} samples, STREAMINFO says {total}")
    return rate, bps, samples


def to_wav(flac_path, wav_path):
    rate, bps, samples = decode(flac_path)
    if bps > 16:
        samples = samples >> (bps - 16)
    elif bps < 16:
        samples = samples << (16 - bps)
    with wave.open(str(wav_path), "wb") as w:
        w.setnchannels(samples.shape[1])
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(samples.astype("<i2").tobytes())
    return rate, samples.shape[1], len(samples)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    print(to_wav(sys.argv[1], sys.argv[2]))

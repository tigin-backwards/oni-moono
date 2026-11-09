#!/usr/bin/env python3
"""TEST INFRASTRUCTURE: reference texels for the engine's block decoders (BlockDecodingTests).

Random blocks of each format (BC6H and BC7 with every mode forced in turn), decoded by
texture2ddecoder (AssetStudio's decoders, pip install texture2ddecoder) and, for BC6H and BC7, by
Pillow as a second reference. ASTC blocks are instead those ARM's astcenc (pip install
astc-encoder-py) encodes from varied images at several quality presets, with astcenc's own decode as
the reference: random ASTC blocks have no reference (texture2ddecoder validates nothing, and astcenc
rejects some blocks the specification decodes). PVRTC texels depend on neighbouring words, so its
entries are whole random levels ("PVRTC_RGBA4 16x8"), square and not, whose "block" is the level. Output (little-endian): per format, its name (byte length, ASCII),
the block size in bytes, the block's width and height in texels, the block count, whether Pillow's
texels follow (0 or 1), the reference's bytes per texel (4: BGRA8; 8: RGBA half floats, for ASTC
HDR), then per block its bytes and the references' texels (row-major, top row first; then Pillow's,
RGBA8, where present).

Usage: make.py OUT
"""

import random, struct, sys
import texture2ddecoder as t
import astc_encoder as astc
from PIL import Image

rng = random.Random(20261019)


def blocks(size, count, force=None):
    for _ in range(count):
        b = bytearray(rng.getrandbits(8) for _ in range(size))
        if force:
            force(b)
        yield bytes(b)


def bc7_mode(m):
    def force(b):
        b[0] = (b[0] & ~((2 << m) - 1) & 0xFF) | (1 << m)

    return force


def bc6_mode(bits, width):
    def force(b):
        mask = (1 << width) - 1
        b[0] = (b[0] & ~mask & 0xFF) | bits

    return force


def pillow(block, n):
    im = Image.frombytes("RGBA" if n == 7 else "RGB", (4, 4), block, "bcn", (n,)).convert("RGBA")
    return im.tobytes()


formats = []  # (name, block bytes, blocks, decode(block), Pillow's BCn number or None, block width, height)
bc7 = [b for m in range(8) for b in blocks(16, 64, bc7_mode(m))] + list(blocks(16, 8, lambda b: b.__setitem__(0, 0)))
formats.append(("BC7", 16, bc7, lambda b: t.decode_bc7(b, 4, 4), 7, 4, 4))
bc6_codes = [(0, 2), (1, 2)] + [(c, 5) for c in (0x02, 0x06, 0x0A, 0x0E, 0x12, 0x16, 0x1A, 0x1E, 0x03, 0x07, 0x0B, 0x0F, 0x13, 0x17, 0x1B, 0x1F)]
bc6 = [b for c, w in bc6_codes for b in blocks(16, 64, bc6_mode(c, w))]
formats.append(("BC6H", 16, bc6, lambda b: t.decode_bc6(b, 4, 4), 6, 4, 4))
for name, size, fn in [
    ("ETC_RGB4", 8, t.decode_etc1),
    ("ETC2_RGB", 8, t.decode_etc2),
    ("ETC2_RGBA1", 8, t.decode_etc2a1),
    ("ETC2_RGBA8", 16, t.decode_etc2a8),
    ("EAC_R", 8, t.decode_eacr),
    ("EAC_R_SIGNED", 8, t.decode_eacr_signed),
    ("EAC_RG", 16, t.decode_eacrg),
    ("EAC_RG_SIGNED", 16, t.decode_eacrg_signed),
]:
    formats.append((name, size, list(blocks(size, 200)), lambda b, fn=fn: fn(b, 4, 4), None, 4, 4))


def astc_images(w, h):
    """Images that lead the encoder to many block modes: noise, gradients, few colours,
    greyscale, constant areas (void-extent blocks), with and without alpha."""

    def image(f):
        return bytes(v for y in range(h) for x in range(w) for v in f(x, y))

    noise = lambda x, y: [rng.getrandbits(8) for _ in range(4)]

    def gradient(x, y):
        return [x * 255 // w, y * 255 // h, (x + y) * 127 // (w + h), 255 - x * 255 // w]

    palette = [[rng.getrandbits(8) for _ in range(4)] for _ in range(3)]

    def few(x, y):
        return palette[(x * 7 // w + y * 5 // h) % 3]

    def grey(x, y):
        v = (x * 13 + y * 29 + rng.getrandbits(4)) & 255
        return [v, v, v, 255]

    def flat(x, y):
        return [40, 200, 90, 255] if x < w // 2 else [10, 20, 30, 128]

    def soft(x, y):
        return [min(255, x * 255 // w + rng.getrandbits(3)), 128, min(255, y * 255 // h + rng.getrandbits(3)), 255]

    return [image(f) for f in (noise, gradient, few, grey, flat, soft)]


def astc_blocks(n, count):
    w = h = n * 6
    found = {}
    for quality in (astc.ASTCQualityPreset.FAST, astc.ASTCQualityPreset.MEDIUM, astc.ASTCQualityPreset.EXHAUSTIVE):
        ctx = astc.ASTCContext(astc.ASTCConfig(astc.ASTCProfile.LDR, n, n, 1, quality, 0))
        for data in astc_images(w, h):
            comp = ctx.compress(astc.ASTCImage(astc.ASTCType.U8, w, h, 1, data), astc.ASTCSwizzle.from_str("RGBA"))
            for i in range(0, len(comp), 16):
                found.setdefault(comp[i : i + 16], None)
    items = list(found)
    rng.shuffle(items)
    return items[:count]


def astc_reference(block, n):
    ctx = astc.ASTCContext(astc.ASTCConfig(astc.ASTCProfile.LDR, n, n, 1, astc.ASTCQualityPreset.MEDIUM, astc.ASTCConfigFlags.DECOMPRESS_ONLY))
    # 8-bit output: astcenc's float output passes through half precision.
    image = ctx.decompress(block, astc.ASTCImage(astc.ASTCType.U8, n, n, 1), astc.ASTCSwizzle.from_str("RGBA"))
    data = bytes(image.data)
    return b"".join(bytes((data[i + 2], data[i + 1], data[i], data[i + 3])) for i in range(0, len(data), 4))


for n in (4, 5, 6, 8, 10, 12):
    formats.append((f"ASTC_{n}x{n}", 16, astc_blocks(n, 160), lambda b, n=n: astc_reference(b, n), None, n, n))


def astc_hdr_images(w, h):
    """Float images: bright and dark HDR ranges, LDR-range areas, alpha varying or HDR."""

    def image(f):
        return struct.pack(f"<{w * h * 4}f", *(v for y in range(h) for x in range(w) for v in f(x, y)))

    def bright(x, y):
        return [2.0 ** (x * 12 / w - 4), 2.0 ** (y * 10 / h - 2), 0.5 + rng.random() * 30, 1.0]

    def mixed(x, y):
        return [rng.random() * (500 if (x // 3 + y // 2) % 2 else 1), rng.random(), rng.random() * 4, rng.random()]

    def grey(x, y):
        v = 2.0 ** (((x + y) % 16) - 6)
        return [v, v, v, 1.0]

    def alpha(x, y):
        return [rng.random() * 8, rng.random() * 8, rng.random() * 8, 2.0 ** (x * 6 / w - 3)]

    return [image(f) for f in (bright, mixed, grey, alpha)]


def astc_hdr_blocks(n, count):
    w = h = n * 6
    found = {}
    for profile in (astc.ASTCProfile.HDR, astc.ASTCProfile.HDR_RGB_LDR_A):
        for quality in (astc.ASTCQualityPreset.FAST, astc.ASTCQualityPreset.THOROUGH):
            ctx = astc.ASTCContext(astc.ASTCConfig(profile, n, n, 1, quality, 0))
            for data in astc_hdr_images(w, h):
                comp = ctx.compress(astc.ASTCImage(astc.ASTCType.F32, w, h, 1, data), astc.ASTCSwizzle.from_str("RGBA"))
                for i in range(0, len(comp), 16):
                    found.setdefault(comp[i : i + 16], None)
    items = list(found)
    rng.shuffle(items)
    return items[:count]


def astc_hdr_reference(block, n):
    ctx = astc.ASTCContext(astc.ASTCConfig(astc.ASTCProfile.HDR, n, n, 1, astc.ASTCQualityPreset.MEDIUM, astc.ASTCConfigFlags.DECOMPRESS_ONLY))
    return bytes(ctx.decompress(block, astc.ASTCImage(astc.ASTCType.F16, n, n, 1), astc.ASTCSwizzle.from_str("RGBA")).data)


for n in (4, 6, 8, 12):
    formats.append((f"ASTC_HDR_{n}x{n}", 16, astc_hdr_blocks(n, 120), lambda b, n=n: astc_hdr_reference(b, n), None, n, n))
for name, two, sizes in [("PVRTC_RGBA4", False, [(8, 8), (16, 16), (32, 16), (16, 32)]), ("PVRTC_RGBA2", True, [(16, 8), (16, 16), (32, 16), (16, 32)])]:
    for w, h in sizes:
        size = max(w // (8 if two else 4), 2) * max(h // 4, 2) * 8
        formats.append((f"{name} {w}x{h}", size, list(blocks(size, 12)), lambda b, w=w, h=h, two=two: t.decode_pvrtc(b, w, h, two), None, w, h))

with open(sys.argv[1], "wb") as out:
    for name, size, items, fn, pil, bw, bh in formats:
        texel_bytes = 8 if name.startswith("ASTC_HDR") else 4
        out.write(struct.pack("<B", len(name)) + name.encode() + struct.pack("<iiiiBB", size, bw, bh, len(items), 1 if pil else 0, texel_bytes))
        for block in items:
            out.write(block)
            out.write(fn(block))
            if pil:
                out.write(pillow(block, pil))

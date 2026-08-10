#!/usr/bin/env python3
"""
Bakes the Life Detection species icons.

Each output is ONE sprite: a light squircle plate with the creature glyph
composited on top of it. Plate and glyph are never separate at runtime - the
panel draws a single texture and it scales as a unit to whatever size the LCD
gives it.

Source art is Twemoji (CC-BY 4.0, https://github.com/jdecked/twemoji).
Attribution is required and lives in the Workshop description.

    python tools/build_bio_icons.py

Reads PNGs from tools/emoji_src/, writes DXT5 DDS to Textures/Sprites/.
"""

import io
import os
import struct

from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "emoji_src")
OUT = os.path.abspath(os.path.join(HERE, "..", "Textures", "Sprites"))

# Output is 128px. The glyph sits at 72px inside it, leaving a margin wide
# enough that the plate reads as a chip rather than a tight crop.
SIZE = 128
GLYPH = 84
PLATE_INSET = 4

# Superellipse exponent. 2 is a circle, infinity is a square; 4 is the squircle.
SQUIRCLE_N = 4.0
SS = 8  # supersample factor for the plate edge

PLATE_RGB = (210, 210, 210)
PLATE_ALPHA = 255

# Twemoji filenames are the codepoint, lowercase hex, variation selectors dropped.
ICONS = {
    "GT_Bio_Cow":     "1f404.png",   # cow
    "GT_Bio_Horse":   "1f40e.png",   # horse
    # Ram, not the ewe. The ewe is a white body with a yellow face and its
    # silhouette vanished against the plate at species-row size.
    "GT_Bio_Sheep":   "1f40f.png",   # ram
    "GT_Bio_Wolf":    "1f43a.png",   # wolf
    "GT_Bio_Spider":  "1f577.png",   # spider
    # Not fauna. Bots and armed humanoids are detected by the same sensor and must
    # not be drawn as wildlife.
    "GT_Bio_Robot":   "1f916.png",   # robot face
    "GT_Bio_Unknown": "1f43e.png",   # paw prints - unrecognised ORGANISM only
}


def squircle_mask(size, n, inset):
    """Filled superellipse, antialiased by supersampling."""
    big = size * SS
    mask = Image.new("L", (big, big), 0)
    px = mask.load()
    r = (size / 2.0) - inset
    cx = cy = size / 2.0
    for yy in range(big):
        y = (yy + 0.5) / SS - cy
        ay = abs(y / r) ** n
        if ay > 1.0:
            continue
        for xx in range(big):
            x = (xx + 0.5) / SS - cx
            if (abs(x / r) ** n) + ay <= 1.0:
                px[xx, yy] = 255
    return mask.resize((size, size), Image.LANCZOS)


DDSD_MIPMAPCOUNT = 0x20000
DDSCAPS_COMPLEX = 0x8
DDSCAPS_MIPMAP = 0x400000


def save_dds_with_mips(img, path):
    """Write a DXT5 DDS carrying a full mip chain.

    Pillow writes a single level with mipMapCount 0 and no MIPMAP caps bits, and
    SE's texture loader will not display such a sprite - it fails silently, so the
    panel simply draws nothing where the icon should be. Every working sprite in
    the game and in other mods ships a complete chain down to 1x1.

    Pillow has no mipmap support, so each level is encoded separately and the
    payloads are concatenated behind a corrected header.
    """
    levels = []
    w, h = img.size
    lvl = img
    while True:
        buf = io.BytesIO()
        lvl.save(buf, format="DDS", pixel_format="DXT5")
        data = buf.getvalue()
        if not levels:
            header = bytearray(data[:128])
        levels.append(data[128:])
        if lvl.size == (1, 1):
            break
        lvl = lvl.resize((max(1, lvl.width // 2), max(1, lvl.height // 2)), Image.LANCZOS)

    flags = struct.unpack_from("<I", header, 8)[0] | DDSD_MIPMAPCOUNT
    struct.pack_into("<I", header, 8, flags)
    struct.pack_into("<I", header, 28, len(levels))          # dwMipMapCount
    caps = struct.unpack_from("<I", header, 108)[0] | DDSCAPS_COMPLEX | DDSCAPS_MIPMAP
    struct.pack_into("<I", header, 108, caps)

    with open(path, "wb") as f:
        f.write(header)
        for payload in levels:
            f.write(payload)

    return len(levels)


def build():
    os.makedirs(OUT, exist_ok=True)
    mask = squircle_mask(SIZE, SQUIRCLE_N, PLATE_INSET)

    plate = Image.new("RGBA", (SIZE, SIZE), PLATE_RGB + (0,))
    solid = Image.new("RGBA", (SIZE, SIZE), PLATE_RGB + (PLATE_ALPHA,))
    plate = Image.composite(solid, plate, mask)

    missing = []
    for name, fn in ICONS.items():
        path = os.path.join(SRC, fn)
        if not os.path.exists(path):
            missing.append(fn)
            continue

        glyph = Image.open(path).convert("RGBA")
        glyph = glyph.resize((GLYPH, GLYPH), Image.LANCZOS)

        canvas = plate.copy()
        off = (SIZE - GLYPH) // 2
        canvas.alpha_composite(glyph, (off, off))

        dds = os.path.join(OUT, name + ".dds")
        mips = save_dds_with_mips(canvas, dds)

        # The whole point of the alpha channel is the corners. Verify they
        # survived the compression rather than assuming.
        check = Image.open(dds).convert("RGBA")
        corner = check.getpixel((1, 1))[3]
        centre = check.getpixel((SIZE // 2, SIZE // 2))[3]
        print("%-16s %5d bytes  %d mips  alpha corner=%3d centre=%3d %s"
              % (name, os.path.getsize(dds), mips, corner, centre,
                 "OK" if corner < 32 and centre > 200 else "*** ALPHA BAD ***"))

    if missing:
        print("\nMissing source art: " + ", ".join(missing))
        print("Place Twemoji 72x72 PNGs in " + SRC)


if __name__ == "__main__":
    build()

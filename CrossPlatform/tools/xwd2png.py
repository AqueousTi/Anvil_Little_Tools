#!/usr/bin/env python3
"""Convert an XWD dump (see xwd(1)) to PNG without stealing the focus.

gnome-screenshot takes a grab that makes the window manager move on, which closes
the focus-loss dialogs this task verifies; `xwd -root` only reads the framebuffer.

usage: xwd2png.py in.xwd out.png
"""
import struct
import sys

from PIL import Image


def convert(src: str, dst: str) -> None:
    data = open(src, "rb").read()
    h = struct.unpack(">25I", data[:100])
    header_size, version, pixmap_format, depth = h[0], h[1], h[2], h[3]
    width, height = h[4], h[5]
    byte_order, bpp, bytes_per_line = h[7], h[11], h[12]
    visual_class = h[13]
    red_mask, green_mask, blue_mask = h[14], h[15], h[16]
    ncolors = h[19]
    if pixmap_format != 2:
        raise SystemExit(f"unsupported pixmap format {pixmap_format}")
    # The composited root reports visual class 5 (DirectColor) but with the plain
    # 0xff0000/0xff00/0xff TrueColor masks, so both classes decode as BGRA.
    if visual_class not in (4, 5):
        raise SystemExit(f"unsupported visual class {visual_class}")
    if (red_mask, green_mask, blue_mask) != (0xFF0000, 0xFF00, 0xFF):
        raise SystemExit(f"unsupported channel masks {red_mask:08x}/{green_mask:08x}/{blue_mask:08x}")
    offset = header_size + ncolors * 12
    stride = bytes_per_line
    # A 24bpp dump still stores one 32-bit unit per pixel (bitmap_unit 32).
    nbytes = 4 if bpp <= 32 and bpp > 16 else max(1, (bpp + 7) // 8)
    raw = bytearray()
    for y in range(height):
        row = data[offset + y * stride: offset + (y + 1) * stride]
        raw += row[: width * nbytes]
    if nbytes == 4:
        order = "BGRA" if byte_order == 0 else "ARGB"
        image = Image.frombytes("RGBA", (width, height), bytes(raw), "raw", order)
        image = image.convert("RGB")
    else:
        raise SystemExit(f"unsupported bytes per pixel {nbytes}")
    image.save(dst)
    print(f"{src} -> {dst} ({width}x{height})")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    convert(sys.argv[1], sys.argv[2])

#!/usr/bin/env python3
"""Compares a captured crop against one rectangle of the reference pattern.

Used by capture-tool-order-verify.sh. The shims draw a deterministic per-pixel
ramp as the whole desktop, the app crops the region the overlay selected, and this
script asserts that the crop is exactly that rectangle of the pattern.

It reports where the first difference is (offset and the two RGB values) so a
failing run says whether the crop is shifted, scaled or the wrong region, instead
of only "not equal".

    screenshot-crop-compare.py <pattern.png> <crop.png> <x1> <y1> <x2> <y2>
"""

import sys

from PIL import Image


def main() -> int:
    pattern_path, crop_path = sys.argv[1], sys.argv[2]
    x1, y1, x2, y2 = (int(value) for value in sys.argv[3:7])
    full = Image.open(pattern_path).convert("RGB")
    crop = Image.open(crop_path).convert("RGB")
    expected = full.crop((x1, y1, x2, y2))
    if crop.size != expected.size:
        print(f"size differs: crop={crop.size} expected={expected.size}")
        return 1

    crop_pixels = crop.tobytes()
    expected_pixels = expected.tobytes()
    if crop_pixels == expected_pixels:
        print(
            f"identical: {crop.size[0]}x{crop.size[1]} at "
            f"({x1},{y1})-({x2},{y2}), {len(crop_pixels)} bytes"
        )
        return 0

    for index in range(0, min(len(crop_pixels), len(expected_pixels)), 3):
        if crop_pixels[index:index + 3] != expected_pixels[index:index + 3]:
            pixel = index // 3
            x = pixel % crop.size[0]
            y = pixel // crop.size[0]
            got = tuple(crop_pixels[index:index + 3])
            want = tuple(expected_pixels[index:index + 3])
            print(
                f"first difference at crop pixel ({x},{y}) "
                f"[desktop ({x1 + x},{y1 + y})]: got {got}, expected {want}"
            )
            break
    return 1


if __name__ == "__main__":
    raise SystemExit(main())

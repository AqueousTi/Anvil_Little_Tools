#!/usr/bin/env python3
"""Local test helper (dev-only): measure a rendered panel and its text.

  panel-metrics.py <png> <x> <y> <w> <h> [label]

Reports the panel colour (median of the region, which is mostly panel), the
brightest glyph pixel (the white text core), the WCAG contrast ratio between
them, and whether the glyph edges carry colour fringing (subpixel AA) or are
neutral (grayscale AA).
"""
import sys
from PIL import Image


def srgb_to_linear(value):
    c = value / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def luminance(pixel):
    r, g, b = (srgb_to_linear(v) for v in pixel[:3])
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast(a, b):
    la, lb = luminance(a), luminance(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


def median_color(pixels):
    channels = list(zip(*[p[:3] for p in pixels]))
    return tuple(sorted(c)[len(c) // 2] for c in channels)


def main():
    path, x, y, w, h = sys.argv[1], *map(int, sys.argv[2:6])
    label = sys.argv[6] if len(sys.argv) > 6 else path
    image = Image.open(path).convert("RGB")
    crop = image.crop((x, y, x + w, y + h))
    pixels = list(crop.getdata())
    panel = median_color(pixels)
    text = max(pixels, key=luminance)
    darkest = min(pixels, key=luminance)
    print(f"{label}:")
    print(f"  panel        rgb{panel}  lum={luminance(panel):.4f}")
    print(f"  text core    rgb{text}  lum={luminance(text):.4f}")
    print(f"  text dark    rgb{darkest}")
    print(f"  contrast     {contrast(panel, text):.2f}:1")
    top = median_color(list(crop.crop((0, 0, w, 6)).getdata()))
    bottom = median_color(list(crop.crop((0, h - 6, w, h)).getdata()))
    print(f"  gradient     top rgb{top} -> bottom rgb{bottom}")

    # Optional AA probe: a region that only holds neutral (white/grey) glyphs.
    # Edge pixels there are neutral under grayscale AA and carry a channel
    # spread when the rasteriser uses subpixel (LCD) antialiasing.
    if len(sys.argv) >= 11:
        ax, ay, aw, ah = map(int, sys.argv[7:11])
        probe = image.crop((ax, ay, ax + aw, ay + ah))
        probe_pixels = list(probe.getdata())
        probe_panel = median_color(probe_pixels)
        probe_text = max(probe_pixels, key=luminance)
        lo, hi = luminance(probe_panel), luminance(probe_text)
        low, high = min(lo, hi), max(lo, hi)
        span = high - low
        edges = [p for p in probe_pixels
                 if low + 0.05 * span < luminance(p) < high - 0.05 * span]
        chroma = max((max(p) - min(p) for p in edges), default=0)
        print(f"  AA probe     panel rgb{probe_panel} glyph rgb{probe_text} "
              f"edges={len(edges)} max channel spread={chroma} "
              f"({'subpixel AA' if chroma > 8 else 'grayscale AA'})")


main()

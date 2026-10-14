#!/usr/bin/env python3
"""Plants a deterministic picture on screen and shows it 1:1 in a borderless window.

The capture harnesses need a screen region whose content is known, so that the
app's cropped result can be compared byte for byte. A real desktop changes under
the harness (a clock, a notification, a chat window), so the harness shows its own
picture instead.

    screenshot-pattern-window.py generate <width> <height> <out.png> [dark]
    screenshot-pattern-window.py show <x> <y> <width> <height> <pattern.png>

``dark`` makes a near-black picture: the flash measurement needs a patch that is
dark while idle, because a full-screen white lightbox is only visible as a jump
when the baseline is low.
"""

import sys

from PIL import Image


def generate(width: int, height: int, out: str, dark: bool) -> None:
    if dark:
        # Near black with a few coloured marks; a white flash takes the mean from
        # ~20 to ~255, which no compression or rounding can hide.
        image = Image.new("RGB", (width, height), (20, 20, 20))
        # A deterministic marker so a shifted or scaled patch is noticeable.
        for x in range(0, width, 32):
            for y in range(0, min(8, height)):
                image.putpixel((x, y), (255, 0, 0))
        image.save(out)
        return

    # The same ramp capture-tool-order-verify.sh uses: red steps per pixel and per
    # 256 columns, blue per 256 rows, green per row, so any shift or scale shows up
    # at the first differing pixel.
    red = bytes((((x & 0xFF) + (x >> 8) * 53) & 0xFF) for x in range(width))
    blue = {
        offset: bytes((((x * 11) + offset) & 0xFF) for x in range(width))
        for offset in range(0, 256, 37)
    }
    for y in range(height):
        row = bytearray(width * 3)
        row[0::3] = red
        row[1::3] = bytes([y & 0xFF]) * width
        row[2::3] = blue[(y >> 8) * 37]
        rows.append(bytes(row))
    Image.frombytes("RGB", (width, height), b"".join(rows)).save(out)


def show(x: int, y: int, width: int, height: int, pattern: str) -> None:
    import gi

    gi.require_version("Gtk", "3.0")
    gi.require_version("Gdk", "3.0")
    from gi.repository import Gdk, Gtk

    window = Gtk.Window()
    window.set_decorated(False)
    window.set_default_size(width, height)
    window.move(x, y)
    window.set_keep_above(True)
    window.set_type_hint(Gdk.WindowTypeHint.UTILITY)
    window.set_resizable(False)
    image = Gtk.Image.new_from_file(pattern)
    # A GtkImage honours the file's pixel size exactly, so the window content is
    # the pattern 1:1 rather than a re-scaled copy.
    window.add(image)
    window.show_all()
    Gtk.main()


def main() -> int:
    mode = sys.argv[1]
    if mode == "generate":
        generate(int(sys.argv[2]), int(sys.argv[3]), sys.argv[4], len(sys.argv) > 5 and sys.argv[5] == "dark")
        return 0
    if mode == "show":
        show(*(int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])), sys.argv[6])
        return 0
    raise SystemExit(f"unknown mode {mode!r} (generate|show)")


if __name__ == "__main__":
    raise SystemExit(main())

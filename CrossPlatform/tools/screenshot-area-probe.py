#!/usr/bin/env python3
"""A flash-free frame grabber / brightness sampler for the real X display.

Why this exists: gnome-screenshot, the portal and the tool chain all flash a
full-screen white lightbox. Measuring "did the flash appear?" with any of them
would be measured with the very thing under test. GNOME Shell's
``org.gnome.Shell.Screenshot.ScreenshotArea`` with ``flash=false`` does not flash,
and a 64x64 area call takes ~10ms, so it can sample the screen fast enough to catch
GNOME Shell's ~250ms Flashspot.

Like the assistant, it leases ``org.gnome.Screenshot`` because GNOME Shell only
accepts calls from an allow-listed D-Bus name (gnome-shell 46,
``js/misc/util.js``). ``ScreenshotArea`` is deliberately NOT ``Screenshot``: a full
capture takes ~1.5s (a 5MB PNG), which would be far too slow.

Modes
-----
    screenshot-area-probe.py area  <x> <y> <w> <h> <out.png>
        One flash-free capture of the rectangle (used as the reference picture).

    screenshot-area-probe.py watch <x> <y> <w> <h> <seconds> <out.csv>
        Samples the rectangle as fast as it can and writes ``t,mean`` lines.
        A full-screen flash shows up as a jump of ~190 in the mean brightness.

Python 3 + PyGObject + Pillow are used on purpose: this is a diagnostic tool, not
part of the shipped application.
"""

import sys
import time

import gi

gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib  # noqa: E402

from PIL import Image  # noqa: E402

TRUSTED_NAME = "org.gnome.Screenshot"
SERVICE = "org.gnome.Shell.Screenshot"
OBJECT_PATH = "/org/gnome/Shell/Screenshot"
INTERFACE = "org.gnome.Shell.Screenshot"


def connect():
    bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
    reply = bus.call_sync(
        "org.freedesktop.DBus",
        "/org/freedesktop/DBus",
        "org.freedesktop.DBus",
        "RequestName",
        GLib.Variant("(su)", (TRUSTED_NAME, 0)),
        None,
        Gio.DBusCallFlags.NONE,
        5000,
        None,
    )
    code = reply.unpack()[0]
    if code != 1:
        raise SystemExit(
            f"could not lease {TRUSTED_NAME} (RequestName code {code}); "
            "another process owns it"
        )
    return bus


def capture(bus, x, y, w, h, path):
    bus.call_sync(
        SERVICE,
        OBJECT_PATH,
        INTERFACE,
        "ScreenshotArea",
        GLib.Variant("(iiiibs)", (x, y, w, h, False, path)),
        None,
        Gio.DBusCallFlags.NONE,
        10000,
        None,
    )


def brightness(path):
    image = Image.open(path).convert("RGB")
    pixels = image.tobytes()
    return sum(pixels) / len(pixels)


def main() -> int:
    mode = sys.argv[1]
    x, y, w, h = (int(value) for value in sys.argv[2:6])
    bus = connect()
    path = "/dev/shm/lt-area-probe.png"
    if mode == "area":
        capture(bus, x, y, w, h, path)
        Image.open(path).save(sys.argv[6])
        return 0
    if mode == "watch":
        seconds = float(sys.argv[6])
        out = sys.argv[7]
        start = time.time()
        samples = []
        while time.time() - start < seconds:
            try:
                capture(bus, x, y, w, h, path)
                samples.append((time.time() - start, brightness(path)))
            except Exception as exception:  # keep sampling; the shell may be busy
                samples.append((time.time() - start, -1.0))
                print(f"sample failed: {exception}", file=sys.stderr)
        with open(out, "w", encoding="utf-8") as handle:
            for stamp, value in samples:
                handle.write(f"{stamp:.4f},{value:.2f}\n")
        return 0
    raise SystemExit(f"unknown mode {mode!r} (area|watch)")


if __name__ == "__main__":
    raise SystemExit(main())

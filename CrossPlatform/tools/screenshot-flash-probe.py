#!/usr/bin/env python3
"""Measures whether a capture path flashes a full-screen white lightbox.

Method
------
A 64x64 patch of the screen is sampled every ~10ms with GNOME Shell's
``ScreenshotArea`` and ``flash=false``. That call itself does not flash (which is
the whole point: measuring a flash with a flashing tool measures the tool), and it
is fast enough to catch GNOME Shell's ~250ms ``Flashspot``.

The patch has to be dark while idle, so ``screenshot-pattern-window.py generate
... dark`` plants a near-black picture and ``show`` displays it. A full-screen
flash then shows up as a jump of ~190 in the mean brightness of the patch.

Trials (each repeated, and retried when the sampler collided with the shell's
one-operation-per-sender rule):
  idle        - baseline, nothing triggered
  shell-off   - org.gnome.Shell.Screenshot.Screenshot(flash=false), the backend
                under test
  shell-on    - the same call with flash=true. This is byte-identical to what
                gnome-screenshot and the GNOME portal ask the shell for, so it is
                the positive control that proves the measurement can see a flash
  portal      - org.freedesktop.portal.Screenshot with interactive=false
  flash-area  - org.gnome.Shell.Screenshot.FlashArea on the whole screen, a direct
                positive control for the detector itself

    screenshot-flash-probe.py <x> <y> <w> <h> <out.txt>
"""

import os
import statistics
import subprocess
import sys
import threading
import time

import gi

gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib  # noqa: E402

from PIL import Image  # noqa: E402

TRUSTED_NAME = "org.gnome.Screenshot"
SHELL_SERVICE = "org.gnome.Shell.Screenshot"
SHELL_PATH = "/org/gnome/Shell/Screenshot"
SHELL_INTERFACE = "org.gnome.Shell.Screenshot"
PORTAL_SERVICE = "org.freedesktop.portal.Desktop"
PORTAL_PATH = "/org/freedesktop/portal/desktop"
PORTAL_INTERFACE = "org.freedesktop.portal.Screenshot"

WHITE_JUMP = 120.0
SAMPLE_PATH = "/dev/shm/lt-flash-probe.png"


def pictures_directory():
    """Where the portal drops its numbered screenshots."""
    try:
        result = subprocess.run(
            ["xdg-user-dir", "PICTURES"], capture_output=True, text=True, timeout=5
        )
        if result.stdout.strip():
            return result.stdout.strip()
    except Exception:
        pass
    return os.path.join(os.path.expanduser("~"), "Pictures")


def snapshot(directory):
    try:
        return set(os.listdir(directory))
    except OSError:
        return set()


def remove_new_files(directory, before):
    """Deletes what a portal trial just wrote; the probe has no use for it."""
    removed = []
    try:
        for name in os.listdir(directory):
            if name in before:
                continue
            path = os.path.join(directory, name)
            if os.path.isfile(path):
                os.remove(path)
                removed.append(path)
    except OSError:
        pass
    return removed


class Probe:
    def __init__(self, x, y, w, h):
        self.x, self.y, self.w, self.h = x, y, w, h
        self.bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        code = self.bus.call_sync(
            "org.freedesktop.DBus",
            "/org/freedesktop/DBus",
            "org.freedesktop.DBus",
            "RequestName",
            GLib.Variant("(su)", (TRUSTED_NAME, 0)),
            None,
            Gio.DBusCallFlags.NONE,
            5000,
            None,
        ).unpack()[0]
        if code != 1:
            raise SystemExit(f"could not lease {TRUSTED_NAME} (code {code})")
        self.failures = 0
        self.samples = 0
        self.trigger_error = None

    def sample(self):
        self.samples += 1
        try:
            self.bus.call_sync(
                SHELL_SERVICE,
                SHELL_PATH,
                SHELL_INTERFACE,
                "ScreenshotArea",
                GLib.Variant("(iiiibs)", (self.x, self.y, self.w, self.h, False, SAMPLE_PATH)),
                None,
                Gio.DBusCallFlags.NONE,
                5000,
                None,
            )
            pixels = Image.open(SAMPLE_PATH).tobytes()
            return sum(pixels) / len(pixels)
        except Exception:
            self.failures += 1
            return -1.0

    def collect(self, seconds):
        values = []
        end = time.time() + seconds
        while time.time() < end:
            values.append(self.sample())
        return values

    # --- triggers -------------------------------------------------------------

    def trigger_shell(self, flash):
        self.bus.call_sync(
            SHELL_SERVICE,
            SHELL_PATH,
            SHELL_INTERFACE,
            "Screenshot",
            GLib.Variant("(bbs)", (False, flash, "/dev/shm/lt-flash-%s.png" % flash)),
            None,
            Gio.DBusCallFlags.NONE,
            15000,
            None,
        )

    def trigger_shell_threaded(self, flash):
        # The shell rejects a second operation from the same sender, and the
        # sampler is on the same connection (only one connection can own the
        # trusted name), so a trigger can lose that race. That is reported so the
        # trial is retried instead of being read as "no flash".
        self.trigger_error = None

        def run():
            try:
                self.trigger_shell(flash)
            except Exception as exception:
                self.trigger_error = exception

        thread = threading.Thread(target=run, daemon=True)
        thread.start()
        return thread

    def trigger_portal(self, index):
        self.bus.call_sync(
            PORTAL_SERVICE,
            PORTAL_PATH,
            PORTAL_INTERFACE,
            "Screenshot",
            GLib.Variant(
                "(sa{sv})",
                (
                    "",
                    {
                        "interactive": GLib.Variant("b", False),
                        "handle_token": GLib.Variant("s", "ltflash%d" % index),
                    },
                ),
            ),
            None,
            Gio.DBusCallFlags.NONE,
            10000,
            None,
        )

    def trigger_flash_area(self):
        self.bus.call_sync(
            SHELL_SERVICE,
            SHELL_PATH,
            SHELL_INTERFACE,
            "FlashArea",
            GLib.Variant("(iiii)", (0, 0, 4480, 1440)),
            None,
            Gio.DBusCallFlags.NONE,
            5000,
            None,
        )


def run_trial(probe, name, trigger, duration, report):
    # A retry keeps the shell's "one operation per sender" collision from looking
    # like a measurement.
    for attempt in range(5):
        baseline = probe.collect(0.5)
        base = statistics.median([value for value in baseline if value >= 0] or [0.0])
        failures_before = probe.failures
        if trigger:
            # Let the sampler's current call finish so the trigger does not lose the
            # shell's one-operation-per-sender race (the flash fires after the
            # picture is taken, so sampling must keep running).
            time.sleep(0.03)
        pictures = pictures_directory()
        pictures_before = snapshot(pictures)
        watchdog = trigger() if trigger else None
        series = probe.collect(duration)
        if watchdog is not None:
            watchdog.join(timeout=20)
        removed = remove_new_files(pictures, pictures_before)
        if removed:
            print(f"{name}: removed {len(removed)} portal screenshot(s) from {pictures}", flush=True)
        collisions = probe.failures - failures_before
        if trigger and probe.trigger_error is not None:
            print(f"{name}: retry {attempt + 1} (the capture itself was refused: {probe.trigger_error})", flush=True)
            time.sleep(0.5)
            continue
        if collisions <= len(series) // 4 or not trigger:
            values = [value for value in series if value >= 0]
            whites = sum(1 for value in values if value > base + WHITE_JUMP)
            record = {
                "trial": name,
                "baseline": round(base, 1),
                "max": round(max(values), 1) if values else -1,
                "samples": len(values),
                "white": whites,
                "verdict": "FLASH" if whites else "no flash",
            }
            report.append(record)
            print(
                "%-12s baseline=%6.1f max=%6.1f white_frames=%3d/%3d  => %s"
                % (name, record["baseline"], record["max"], whites, len(values), record["verdict"]),
                flush=True,
            )
            return
        print(f"{name}: retry {attempt + 1} (sampler collided with the capture)", flush=True)
        time.sleep(0.5)
    print(f"{name}: could not get a clean run", flush=True)


def main():
    x, y, w, h = (int(value) for value in sys.argv[1:5])
    out = sys.argv[5]
    probe = Probe(x, y, w, h)
    report = []
    run_trial(probe, "idle", None, 1.2, report)
    run_trial(probe, "shell-off", lambda: probe.trigger_shell_threaded(False), 3.0, report)
    run_trial(probe, "shell-on", lambda: probe.trigger_shell_threaded(True), 3.0, report)
    for index in range(2):
        run_trial(probe, "portal-%d" % (index + 1), lambda index=index: probe.trigger_portal(index), 4.0, report)
    run_trial(probe, "flash-area", lambda: probe.trigger_flash_area(), 2.0, report)

    with open(out, "w", encoding="utf-8") as handle:
        for record in report:
            handle.write(
                "%(trial)s baseline=%(baseline)s max=%(max)s samples=%(samples)s white=%(white)s %(verdict)s\n"
                % record
            )


if __name__ == "__main__":
    main()

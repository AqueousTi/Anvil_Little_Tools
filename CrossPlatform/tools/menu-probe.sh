#!/usr/bin/env bash
# Local test helper (dev-only): open the real GNOME tray menu of the workspace
# build against a pure black backdrop (kept below the 32px top bar), click one
# row, and report the menu's state before and after.
#   menu-probe.sh <row y> <label>
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
y="$1"; label="$2"; out="$ROOT/.tools/out"; export DISPLAY="${DISPLAY:-:1}"
count() { python3 - "$1" <<'PY'
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert("RGB").crop((2100, 40, 2400, 560))
print(sum(1 for p in im.getdata() if sum(p)/3 > 200))
PY
}
shot() { gnome-screenshot -f "$1" >/dev/null 2>&1; }

pkill -x backdrop 2>/dev/null
setsid "$ROOT/.tools/backdrop" black 2560 1440 >"$out/.bd-probe.log" 2>&1 < /dev/null &
sleep 2.2
shot "$out/.mp-closed.png"
base="$(count "$out/.mp-closed.png")"
[[ "$base" != "0" ]] && { "$ROOT/.tools/x11tool" click 2237 16 >/dev/null; sleep 2.2; shot "$out/.mp-closed.png"; base="$(count "$out/.mp-closed.png")"; }
echo "[$label] backdrop baseline=$base"
"$ROOT/.tools/x11tool" move 2237 16 >/dev/null; sleep 0.5
"$ROOT/.tools/x11tool" click 2237 16 >/dev/null; sleep 2.5
shot "$out/$label-open.png"
echo "[$label] menu open=$(count "$out/$label-open.png")"

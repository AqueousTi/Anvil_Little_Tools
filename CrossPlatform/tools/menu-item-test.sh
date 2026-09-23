#!/usr/bin/env bash
# Local test helper (dev-only): open the real tray menu, click one item, and
# report whether the menu is still open afterwards.
#   menu-item-test.sh <label> <itemY>
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
label="$1"; itemy="$2"; out="$ROOT/.tools/out"; export DISPLAY="${DISPLAY:-:1}"
bright() { python3 - "$1" <<'PY'
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert("RGB").crop((2100, 40, 2400, 560))
print(sum(1 for p in im.getdata() if sum(p)/3 > 200))
PY
}
shot() { gnome-screenshot -f "$1" >/dev/null 2>&1; }
# make sure nothing is open: click the icon twice with a pause
"$ROOT/.tools/x11tool" move 2237 16 >/dev/null; sleep 0.5
"$ROOT/.tools/x11tool" click 2237 16 >/dev/null; sleep 2.2
shot "$out/.mi-probe.png"
if [[ "$(bright "$out/.mi-probe.png")" == "0" ]]; then
  "$ROOT/.tools/x11tool" click 2237 16 >/dev/null; sleep 2.2
fi
shot "$out/$label-1-open.png"
echo "[$label] open: $(bright "$out/$label-1-open.png")"
"$ROOT/.tools/x11tool" move 2240 "$itemy" >/dev/null; sleep 0.4
"$ROOT/.tools/x11tool" click 2240 "$itemy" >/dev/null; sleep 2.5
shot "$out/$label-2-after.png"
echo "[$label] after item y=$itemy: $(bright "$out/$label-2-after.png")"

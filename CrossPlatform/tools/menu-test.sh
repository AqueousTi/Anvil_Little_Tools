#!/usr/bin/env bash
# Local test helper (dev-only): drive the *real* GNOME status notifier menu for
# the workspace build and report whether it stays open across switch clicks.
#   menu-test.sh <label>
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
label="${1:-run}"; out="$ROOT/.tools/out"; export DISPLAY="${DISPLAY:-:1}"
bright() { python3 - "$1" <<'PY'
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert("RGB").crop((2100, 40, 2400, 560))
print(sum(1 for p in im.getdata() if sum(p)/3 > 200))
PY
}
shot() { gnome-screenshot -f "$1" >/dev/null 2>&1; }
icon=${ICON:-2237}; item_stock=327; item_todo=287

pkill -x backdrop 2>/dev/null
setsid "$ROOT/.tools/backdrop" black 2560 1440 >"$out/.bd-menu.log" 2>&1 < /dev/null &
echo $! > "$out/.backdrop.pid"
sleep 1.5
bd="$(grep -oE '0x[0-9a-f]+' "$out/.bd-menu.log" | head -1)"
[[ -n "$bd" ]] && "$ROOT/.tools/xraisetool" "$bd" >/dev/null
sleep 0.6

shot "$out/.menu-baseline.png"
echo "[$label] baseline bright pixels: $(bright "$out/.menu-baseline.png")"
"$ROOT/.tools/x11tool" move $icon 16 >/dev/null; sleep 0.6
"$ROOT/.tools/x11tool" click $icon 16 >/dev/null; sleep 2.5
shot "$out/$label-1-open.png"
echo "[$label] after opening the menu:   $(bright "$out/$label-1-open.png")"

"$ROOT/.tools/x11tool" move 2240 $item_stock >/dev/null; sleep 0.4
"$ROOT/.tools/x11tool" click 2240 $item_stock >/dev/null; sleep 2.5
shot "$out/$label-2-after-stock-toggle.png"
echo "[$label] after clicking 股票观察:   $(bright "$out/$label-2-after-stock-toggle.png")"
echo "[$label] manager: $(tr -d '\n ' < "$ROOT/.tools/stkverify/config/little-tools/manager.json" | grep -o '"StockEnabled":[a-z]*')"

"$ROOT/.tools/x11tool" move 2240 $item_todo >/dev/null; sleep 0.4
"$ROOT/.tools/x11tool" click 2240 $item_todo >/dev/null; sleep 2.5
shot "$out/$label-3-after-todo-toggle.png"
echo "[$label] after clicking 每日待办:   $(bright "$out/$label-3-after-todo-toggle.png")"
echo "[$label] manager: $(tr -d '\n ' < "$ROOT/.tools/stkverify/config/little-tools/manager.json" | grep -o '"TodoNotesEnabled":[a-z]*')"

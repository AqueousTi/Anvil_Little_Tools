#!/usr/bin/env bash
# Local test helper (dev-only): open the real GNOME tray menu of the workspace
# build over a pure black backdrop, click a named item by its row index, and
# report whether the menu is still open.
#
#   menu-verify.sh <rowIndex 0-based> <label>
# Row order: 翻译 问答 截图翻译 余量监控 AI翻译与快问 每日待办 股票观察 开机自启 打开工具目录 退出
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
row="$1"; label="$2"; icon="${ICON:-2237}"; out="$ROOT/.tools/out"; export DISPLAY="${DISPLAY:-:1}"
count() { python3 - "$1" <<'PY'
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert("RGB").crop((2100, 40, 2400, 560))
print(sum(1 for p in im.getdata() if sum(p)/3 > 200))
PY
}
rows() { python3 - "$1" <<'PY'
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert("RGB")
xs = [x for x in range(2100, 2400) if sum(im.getpixel((x, 100)))/3 > 200]
ys = [y for y in range(40, 560) if sum(im.getpixel((2240, y)))/3 > 200]
runs = []; start = None
for y in range(min(ys), max(ys)):
    dark = sum(1 for x in range(min(xs)+10, max(xs)-10) if sum(im.getpixel((x, y)))/3 < 150)
    if dark > 3 and start is None: start = y
    elif dark <= 3 and start is not None: runs.append((start+y-1)//2); start = None
print(' '.join(str(r) for r in runs))
PY
}
shot() { gnome-screenshot -f "$1" >/dev/null 2>&1; }

pkill -x backdrop 2>/dev/null
setsid "$ROOT/.tools/backdrop" black 2560 1440 >"$out/.bd-verify.log" 2>&1 < /dev/null &
sleep 1.5
bd="$(grep -oE '0x[0-9a-f]+' "$out/.bd-verify.log" | head -1)"
[[ -n "$bd" ]] && "$ROOT/.tools/xraisetool" "$bd" >/dev/null
sleep 0.6
shot "$out/.mv-closed.png"
[[ "$(count "$out/.mv-closed.png")" != "0" ]] && { "$ROOT/.tools/x11tool" click $icon 16 >/dev/null; sleep 2.2; }
"$ROOT/.tools/x11tool" move $icon 16 >/dev/null; sleep 0.5
"$ROOT/.tools/x11tool" click $icon 16 >/dev/null; sleep 2.5
shot "$out/.mv-open.png"
open="$(count "$out/.mv-open.png")"
list=($(rows "$out/.mv-open.png"))
echo "[$label] menu open pixels=$open rows=${list[*]}"
y="${list[$row]:-}"
if [[ -z "$y" ]]; then echo "[$label] row $row not found"; exit 1; fi
echo "[$label] clicking row $row '${label}' at y=$y"
"$ROOT/.tools/x11tool" move 2240 "$y" >/dev/null; sleep 0.5
"$ROOT/.tools/x11tool" click 2240 "$y" >/dev/null; sleep 2.5
shot "$out/$label-after.png"
echo "[$label] menu after click = $(count "$out/$label-after.png")"

#!/usr/bin/env bash
# Local test helper (dev-only): capture the todo capsule and the assistant
# window of a given suite process over a white backdrop, and print the panel
# metrics. Used for the glass panel before/after comparison.
#
#   measure-windows.sh <label> <pid> [--raise-first]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
label="$1"; pid="$2"; shade="${3:-white}"
export DISPLAY="${DISPLAY:-:1}"
out="$ROOT/.tools/out"

win_for() {
  local needle="$1"
  for id in $(xwininfo -root -tree | grep -F "$needle" | awk '{print $1}'); do
    p=$(xprop -id "$id" _NET_WM_PID 2>/dev/null | grep -oE '[0-9]+$')
    [[ "$p" == "$pid" ]] && { echo "$id"; return; }
  done
}

setsid "$ROOT/.tools/backdrop" "$shade" 2560 1440 >"$out/.backdrop-w.log" 2>&1 < /dev/null &
echo $! > "$out/.backdrop.pid"
sleep 1.5
bd="$(grep -oE '0x[0-9a-f]+' "$out/.backdrop-w.log" | head -1)"
"$ROOT/.tools/xraisetool" "$bd" >/dev/null
sleep 0.5

for spec in "assistant:Little Tools AI:ShowTranslation" "todo:Daily Todo:ShowTodo"; do
  name="${spec%%:*}"; rest="${spec#*:}"; title="${rest%%:*}"; show="${rest#*:}"
  # The assistant window hides as soon as it is deactivated, and raising the
  # backdrop deactivates it, so it is shown again after the backdrop is up.
  [[ -n "$show" ]] && bash "$ROOT/CrossPlatform/tools/pipe.sh" "$ROOT/.tools/stkverify/tmp" "$show" >/dev/null 2>&1
  sleep 1.5
  id="$(win_for "$title")"
  if [[ -z "$id" ]]; then echo "$label/$name: window not found"; continue; fi
  "$ROOT/.tools/xraisetool" "$id" >/dev/null
  sleep 1
  raw="$out/.measure-$label-$name.png"
  gnome-screenshot -f "$raw" >/dev/null 2>&1
  read -r x y w h < <(xwininfo -id "$id" | awk '
    /Absolute upper-left X/{x=$4} /Absolute upper-left Y/{y=$4} /Width:/{w=$2} /Height:/{h=$2}
    END{print x, y, w, h}')
  python3 - "$raw" "$out/$name-$label.png" "$x" "$y" "$w" "$h" <<'PY'
import sys
from PIL import Image
raw, out, x, y, w, h = sys.argv[1], sys.argv[2], *map(int, sys.argv[3:7])
Image.open(raw).convert("RGB").crop((max(0, x - 10), max(0, y - 10), x + w + 10, y + h + 10)).save(out)
PY
  python3 "$ROOT/CrossPlatform/tools/panel-metrics.py" "$raw" "$x" "$y" "$w" "$h" "$label/$name"
  [[ "$title" == "Daily Todo" ]] && python3 "$ROOT/CrossPlatform/tools/panel-metrics.py" "$raw" \
    $((x + 14)) $((y + 20)) 100 10 "$label/$name white text probe"
done

kill "$(cat "$out/.backdrop.pid")" 2>/dev/null

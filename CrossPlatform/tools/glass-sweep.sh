#!/usr/bin/env bash
# Local test helper (dev-only): build the app once per panel alpha pair and
# capture the stock capsule, todo capsule and assistant window over a pure white
# and a pure black backdrop, printing the panel colour and contrast for each.
#
#   glass-sweep.sh 9A:B4 B4:C8 C8:D6      (shell:hover alpha, hex)
#
# Restores 0xB4/0xC8 at the end.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/CrossPlatform/tools/env.sh"
export DISPLAY="${DISPLAY:-:1}"
out="$ROOT/.tools/out"
LOG="$out/glass-sweep.txt"
: > "$LOG"

patch() {
  python3 - "$ROOT" "$1" "$2" <<'PY'
import re, sys
root, shell, hover = sys.argv[1], sys.argv[2], sys.argv[3]
p = root + '/CrossPlatform/LittleTools.Assistant/GlassSurface.cs'
s = open(p, encoding='utf-8').read()
s = re.sub(r'public const byte ShellAlpha = 0x[0-9A-Fa-f]{2};',
           f'public const byte ShellAlpha = 0x{shell};', s)
s = re.sub(r'public const byte HoverAlpha = 0x[0-9A-Fa-f]{2};',
           f'public const byte HoverAlpha = 0x{hover};', s)
open(p, 'w', encoding='utf-8').write(s)
# The assistant window hardcodes the same ramp in XAML, plus a code fallback.
p = root + '/CrossPlatform/LittleTools.Assistant/MainWindow.axaml'
x = open(p, encoding='utf-8').read()
x = re.sub(r'Color="#[0-9A-Fa-f]{2}171B23"', f'Color="#{shell}171B23"', x)
x = re.sub(r'Color="#[0-9A-Fa-f]{2}0D1016"', f'Color="#{shell}0D1016"', x)
x = re.sub(r'Color="#[0-9A-Fa-f]{2}1B2029"', f'Color="#{hover}171B23"', x)
x = re.sub(r'Color="#[0-9A-Fa-f]{2}11141B"', f'Color="#{hover}0D1016"', x)
open(p, 'w', encoding='utf-8').write(x)
p = root + '/CrossPlatform/LittleTools.Assistant/MainWindow.axaml.cs'
c = open(p, encoding='utf-8').read()
c = re.sub(r'Brush\.Parse\(hover \? "#[0-9A-Fa-f]{8}" : "#[0-9A-Fa-f]{8}"\)',
           f'Brush.Parse(hover ? "#{hover}171B23" : "#{shell}171B23")', c)
open(p, 'w', encoding='utf-8').write(c)
print(f'patched shell=0x{shell} hover=0x{hover}')
PY
}

capture() {
  local pid="$1" title="$2" label="$3"
  local id=""
  for candidate in $(xwininfo -root -tree | grep -F "$title" | awk '{print $1}'); do
    p=$(xprop -id "$candidate" _NET_WM_PID 2>/dev/null | grep -oE '[0-9]+$')
    [[ "$p" == "$pid" ]] && id="$candidate"
  done
  [[ -z "$id" ]] && { echo "$label: window '$title' not found"; return; }
  "$ROOT/.tools/xraisetool" "$id" >/dev/null
  sleep 0.9
  local raw="$out/.sweep-raw.png"
  gnome-screenshot -f "$raw" >/dev/null 2>&1
  read -r x y w h < <(xwininfo -id "$id" | awk '
    /Absolute upper-left X/{x=$4} /Absolute upper-left Y/{y=$4} /Width:/{w=$2} /Height:/{h=$2}
    END{print x, y, w, h}')
  python3 - "$raw" "$out/$label.png" "$x" "$y" "$w" "$h" <<'PY'
import sys
from PIL import Image
raw, dst, x, y, w, h = sys.argv[1], sys.argv[2], *map(int, sys.argv[3:7])
Image.open(raw).convert("RGB").crop((max(0, x - 10), max(0, y - 10), x + w + 10, y + h + 10)).save(dst)
PY
  python3 "$ROOT/CrossPlatform/tools/panel-metrics.py" "$raw" "$x" "$y" "$w" "$h" "$label"
  if [[ "$title" == *"股票观察"* ]]; then
    python3 "$ROOT/CrossPlatform/tools/panel-metrics.py" "$raw" $((x + 214 - 200)) $((y + 220 - 200)) 100 10 "$label white text probe"
  fi
  # Secondary text: the todo capsule's date/secondary line, stock capsule's metric label.
  if [[ "$title" == *"Daily Todo"* ]]; then
    python3 "$ROOT/CrossPlatform/tools/panel-metrics.py" "$raw" $((x + 14)) $((y + 20)) 100 10 "$label secondary probe"
  fi
}

for pair in "$@"; do
  shell="${pair%%:*}"; hover="${pair##*:}"
  patch "$shell" "$hover"
  ( cd "$ROOT" && dotnet build CrossPlatform/LittleTools.CrossPlatform.slnx -c Release 2>&1 | tail -2 )
  for shade in white black; do
    pkill -x backdrop 2>/dev/null
    bash "$ROOT/CrossPlatform/tools/stkverify.sh" stop >/dev/null 2>&1
    setsid "$ROOT/.tools/backdrop" "$shade" 2560 1440 >"$out/.bd-$shade.log" 2>&1 < /dev/null &
    echo $! > "$out/.backdrop.pid"
    sleep 1.5
    bd="$(grep -oE '0x[0-9a-f]+' "$out/.bd-$shade.log" | head -1)"
    [[ -n "$bd" ]] && "$ROOT/.tools/xraisetool" "$bd" >/dev/null
    sleep 0.5
    LITTLETOOLS_STOCK_FIXTURES=1 bash "$ROOT/CrossPlatform/tools/stkverify.sh" manager \
      '{"MonitorEnabled":true,"TranslateEnabled":true,"TodoNotesEnabled":true,"StockEnabled":true,"EdgeHideMonitor":false,"EdgeHideTranslate":false,"EdgeHideTodo":false,"EdgeHideStock":false}' >/dev/null
    cat > "$ROOT/.tools/stkverify/data/little-tools/stock/settings.json" <<'JSON'
{"Watched":[{"Code":"510300","AlertEnabled":false,"PremiumThreshold":2}],"SelectedCode":"510300","Topmost":false,"Compact":false,"KlinePeriod":"Daily","RangeYears":1,"Left":200,"Top":200}
JSON
    pid="$(LITTLETOOLS_STOCK_FIXTURES=1 bash "$ROOT/CrossPlatform/tools/stkverify.sh" start --background)"
    sleep 2
    bash "$ROOT/CrossPlatform/tools/pipe.sh" "$ROOT/.tools/stkverify/tmp" ShowTranslation >/dev/null
    sleep 1.5
    { capture "$pid" "Little Tools AI" "sweep-$shell-$shade-assistant"
      bash "$ROOT/CrossPlatform/tools/pipe.sh" "$ROOT/.tools/stkverify/tmp" ShowStock >/dev/null; sleep 2.5
      capture "$pid" "股票观察\"" "sweep-$shell-$shade-stock"
      bash "$ROOT/CrossPlatform/tools/pipe.sh" "$ROOT/.tools/stkverify/tmp" ShowTodo >/dev/null; sleep 2.5
      capture "$pid" "Daily Todo" "sweep-$shell-$shade-todo"
    } 2>&1 | tee -a "$LOG"
    bash "$ROOT/CrossPlatform/tools/stkverify.sh" stop >/dev/null 2>&1
    kill "$(cat "$out/.backdrop.pid")" 2>/dev/null
    sleep 0.8
  done
done

patch B4 C8
( cd "$ROOT" && dotnet build CrossPlatform/LittleTools.CrossPlatform.slnx -c Release 2>&1 | tail -2 )
pkill -x backdrop 2>/dev/null
echo "restored shell=0xB4 hover=0xC8"

#!/usr/bin/env bash
# Local test helper (dev-only): capture the stock capsule over a pure white and
# a pure black backdrop on the test display and print the panel/text metrics.
#
#   measure-capsule.sh <label>
#
# The backdrop is a plain managed window, so it is raised above the browser and
# the always-on-top capsule is raised above it.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
label="${1:-run}"
export DISPLAY="${DISPLAY:-:1}"
out="$ROOT/.tools/out"
mkdir -p "$out"
source "$ROOT/CrossPlatform/tools/env.sh"
export XDG_CONFIG_HOME="$ROOT/.tools/stkverify/config" XDG_DATA_HOME="$ROOT/.tools/stkverify/data" \
  XDG_CACHE_HOME="$ROOT/.tools/stkverify/cache" TMPDIR="$ROOT/.tools/stkverify/tmp"
export LITTLETOOLS_STOCK_FIXTURES=1

bash "$ROOT/CrossPlatform/tools/stkverify.sh" stop >/dev/null 2>&1 || true
mkdir -p "$XDG_DATA_HOME/little-tools/stock"
cat > "$XDG_DATA_HOME/little-tools/stock/settings.json" <<'JSON'
{"Watched":[{"Code":"510300","AlertEnabled":false,"PremiumThreshold":2}],"SelectedCode":"510300","Topmost":false,"Compact":false,"KlinePeriod":"Daily","RangeYears":1,"Left":200,"Top":200}
JSON

for shade in white black; do
  setsid "$ROOT/.tools/backdrop" "$shade" 2560 1440 >"$out/.backdrop-$shade.log" 2>&1 < /dev/null &
  echo $! > "$out/.backdrop.pid"
  sleep 1.5
  bd="$(grep -oE '0x[0-9a-f]+' "$out/.backdrop-$shade.log" | head -1)"
  [[ -n "$bd" ]] && "$ROOT/.tools/xraisetool" "$bd" >/dev/null
  sleep 0.5
  pid="$(bash "$ROOT/CrossPlatform/tools/stkverify.sh" start --stock)"
  sleep 3
  cap=""
  for id in $(xwininfo -root -tree | grep '股票观察"' | awk '{print $1}'); do
    p=$(xprop -id "$id" _NET_WM_PID 2>/dev/null | grep -oE '[0-9]+$')
    [[ "$p" == "$pid" ]] && cap="$id"
  done
  "$ROOT/.tools/xraisetool" "$cap" >/dev/null
  sleep 1.2
  raw="$out/.measure-$shade.png"
  gnome-screenshot -f "$raw" >/dev/null 2>&1
  python3 - "$raw" "$out/capsule-$label-$shade.png" <<'PY'
import sys
from PIL import Image
raw, out = sys.argv[1], sys.argv[2]
Image.open(raw).convert("RGB").crop((190, 190, 190 + 336, 190 + 112)).save(out)
PY
  python3 "$ROOT/CrossPlatform/tools/panel-metrics.py" "$raw" 200 200 316 92 "capsule/$label/$shade" 214 220 100 10
  bash "$ROOT/CrossPlatform/tools/stkverify.sh" stop >/dev/null 2>&1
  kill "$(cat "$out/.backdrop.pid")" 2>/dev/null
  sleep 1
done

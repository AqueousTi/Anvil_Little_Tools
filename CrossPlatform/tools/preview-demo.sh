#!/usr/bin/env bash
# Local verification driver (dev-only) for the click-to-magnify screenshot
# translation preview.
#
# Starts the workspace build in hold mode: a real production window with locally
# planted translation images, so no Baidu account or network is needed, then drives
# it with XTest and records window state, cursor readings and screenshots.
#
# usage: preview-demo.sh [evidence-dir]
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
export DISPLAY="${DISPLAY:-:1}"
export TMPDIR="$WORKSPACE_ROOT/.tools/tmp/preview-demo"
export AVALONIA_TELEMETRY_OPTOUT=1
mkdir -p "$TMPDIR" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME"

OUT="${1:-$WORKSPACE_ROOT/.tools/out/preview-demo}"
mkdir -p "$OUT"
APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
X11="$WORKSPACE_ROOT/.tools/x11tool"
PROBE="$WORKSPACE_ROOT/.tools/cursorprobe"
EVIDENCE="$OUT/evidence.txt"
LOG="$OUT/app.log"

: > "$EVIDENCE"
note() { echo "$*" | tee -a "$EVIDENCE"; }

# Only the workspace instance's windows are ours; an installed suite may be
# running on the same display and reports the same titles but a different class.
win_id() {
  xwininfo -root -tree 2>/dev/null | grep -F "\"$1\"" | grep -F '("dotnet"' | head -1 | awk '{print $1}'
}
map_state() { xwininfo -id "$1" 2>/dev/null | grep 'Map State' | awk -F: '{print $2}' | xargs; }
geom() { xwininfo -id "$1" 2>/dev/null | grep -E 'Absolute upper-left|Width:|Height:' | awk -F: '{print $2}' | tr '\n' ' '; }
rect() { xwininfo -id "$1" | awk -F: '/Absolute upper-left X/{x=$2} /Absolute upper-left Y/{y=$2} /^  Width/{w=$2} /^  Height/{h=$2} END{print x, y, w, h}'; }
# xwd -id reads the window itself; a compositing window manager leaves the root
# window without the client pixels, so -root is only used for the desktop shot.
capture_window() {
  xwd -id "$1" -silent -out "$OUT/$2.xwd" 2>/dev/null || return 1
  python3 "$WORKSPACE_ROOT/CrossPlatform/tools/xwd2png.py" "$OUT/$2.xwd" "$OUT/$2.png" >/dev/null || return 1
  rm -f "$OUT/$2.xwd"
}
capture_desktop() { gnome-screenshot -f "$OUT/$1.png" >/dev/null 2>&1; }
crop() {
  python3 - "$1" "$2" "$3" "$4" "$5" "$6" <<'PY'
import sys
from PIL import Image
src, dst, x, y, w, h = sys.argv[1], sys.argv[2], *map(int, sys.argv[3:7])
Image.open(src).crop((max(0, x), max(0, y), x + w, y + h)).save(dst)
PY
}
tile() { # horizontal composite of two windows
  python3 - "$1" "$2" "$3" <<'PY'
import sys
from PIL import Image
a = Image.open(sys.argv[1]).convert("RGB")
b = Image.open(sys.argv[2]).convert("RGB")
canvas = Image.new("RGB", (a.width + b.width + 12, max(a.height, b.height)), (32, 34, 40))
canvas.paste(a, (0, 0))
canvas.paste(b, (a.width + 12, 0))
canvas.save(sys.argv[3])
PY
}

stop() {
  if [[ -n "${app_pid:-}" ]]; then kill "$app_pid" 2>/dev/null; wait "$app_pid" 2>/dev/null; fi
}
trap stop EXIT

note "== start hold instance ($(date +%H:%M:%S)) =="
: > "$LOG"
setsid "$DOTNET_ROOT/dotnet" "$APP" --preview-smoke "$OUT/planted" --preview-hold >>"$LOG" 2>&1 </dev/null &
app_pid=$!
main_id=""
for _ in $(seq 1 40); do
  sleep 0.5
  main_id="$(win_id 'Little Tools AI')"
  [[ -n "$main_id" && "$(map_state "$main_id")" == "IsViewable" ]] && break
done
if [[ -z "$main_id" ]]; then note "FAIL: the translation window never appeared"; cat "$LOG"; exit 1; fi

for _ in $(seq 1 20); do [[ -f "$OUT/planted.metrics.txt" ]] && break; sleep 0.5; done
metrics="$(cat "$OUT/planted.metrics.txt" 2>/dev/null)"
read -r win_x win_y <<<"$(sed -n 's/^windowPositionPx=\([0-9-]*\),\([0-9-]*\)$/\1 \2/p' <<<"$metrics")"
read -r img_cx img_cy <<<"$(sed -n 's/^imageCenterInWindowDip=\([0-9.]*\),\([0-9.]*\)$/\1 \2/p' <<<"$metrics")"
scale="$(sed -n 's/^renderScaling=//p' <<<"$metrics")"
click_x=$(python3 -c "print(int(round($win_x + $img_cx * $scale)))")
click_y=$(python3 -c "print(int(round($win_y + $img_cy * $scale)))")
note "main window id=$main_id geom=$(geom "$main_id")"
note "planted metrics: $(echo "$metrics" | tr '\n' ' ')"
note "click target (result image centre) = $click_x,$click_y"
capture_window "$main_id" "demo-1-result-list"
note "result list window render: demo-1-result-list.png"

note "-- 1. hover the result image --"
"$X11" move "$click_x" "$click_y" >/dev/null
sleep 0.6
note "cursorprobe: $("$PROBE" | head -1)"

note "-- 2. click the result image --"
"$X11" click "$click_x" "$click_y" >/dev/null
preview_id=""
for _ in $(seq 1 20); do sleep 0.3; preview_id="$(win_id 'Little Tools · 译图预览')"; [[ -n "$preview_id" ]] && break; done
if [[ -z "$preview_id" ]]; then note "FAIL: no preview window appeared"; exit 1; fi
note "preview id=$preview_id geom=$(geom "$preview_id")"
note "preview _NET_WM_STATE: $(xprop -id "$preview_id" _NET_WM_STATE 2>/dev/null)"
note "preview transient: $(xprop -id "$preview_id" WM_TRANSIENT_FOR 2>/dev/null)"
note "main window after open: $(map_state "$main_id")"
read -r pv_x pv_y pv_w pv_h <<<"$(rect "$preview_id")"
capture_window "$preview_id" "demo-2-preview-100"
capture_desktop "demo-2-desktop"
crop "$OUT/demo-2-desktop.png" "$OUT/demo-2-with-main.png" 848 404 1230 900

note "-- 3. click + twice --"
plus_x=$((pv_x + 182))
plus_y=$((pv_y + 59))
"$X11" click "$plus_x" "$plus_y" >/dev/null; sleep 0.4
"$X11" click "$plus_x" "$plus_y" >/dev/null; sleep 0.7
capture_window "$preview_id" "demo-3-preview-zoomed"
crop "$OUT/demo-2-preview-100.png" "$OUT/demo-2-zoom-label.png" 188 48 40 18
crop "$OUT/demo-3-preview-zoomed.png" "$OUT/demo-3-zoom-label.png" 188 48 40 18
if cmp -s "$OUT/demo-2-zoom-label.png" "$OUT/demo-3-zoom-label.png"; then
  note "zoom label unchanged after +: FAIL"
else
  note "zoom label changed after two + clicks (see demo-2/3-zoom-label.png)"
fi
tile "$OUT/demo-2-preview-100.png" "$OUT/demo-3-preview-zoomed.png" "$OUT/demo-3-zoom-compare.png"

note "-- 4. Escape --"
"$X11" send Escape none >/dev/null
sleep 1.0
if [[ -n "$(win_id 'Little Tools · 译图预览')" ]]; then note "FAIL: Escape did not close the preview"; else note "escape: preview closed"; fi
note "main window after escape: $(map_state "$main_id")"
capture_window "$main_id" "demo-4-main-after-escape"

note "-- 5. reopen, then take the focus away with another application --"
"$X11" click "$click_x" "$click_y" >/dev/null
preview_id=""
for _ in $(seq 1 20); do sleep 0.3; preview_id="$(win_id 'Little Tools · 译图预览')"; [[ -n "$preview_id" ]] && break; done
note "preview reopened: $([[ -n "$preview_id" ]] && echo yes || echo no)"
# The stock capsule of the already installed suite on this display belongs to a
# different process, so clicking it is a real cross-application focus change.
other="$(xwininfo -root -tree 2>/dev/null | grep -F 'Little Tools · 股票观察' | grep -v '("dotnet"' | head -1)"
if [[ -n "$other" ]]; then
  read -r ox oy ow oh <<<"$(python3 - "$other" <<'PY'
import re, sys
m = re.search(r'(\d+)x(\d+)\+(-?\d+)\+(-?\d+)', sys.argv[1])
print(m.group(3), m.group(4), m.group(1), m.group(2))
PY
)"
  "$X11" click "$((ox + ow / 2))" "$((oy + oh / 2))" >/dev/null
  sleep 1.5
  if [[ -n "$(win_id 'Little Tools · 译图预览')" ]]; then note "FAIL: the preview survived losing the focus"; else note "focus loss: preview closed"; fi
else
  note "SKIP: no second application window to take the focus"
fi
note "main window after focus loss: $(map_state "$main_id")"

note "== done =="

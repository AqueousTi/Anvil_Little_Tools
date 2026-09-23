#!/usr/bin/env bash
# Capture a real on-screen screenshot of the Little Tools assistant window.
#
# The DSH sandbox blocks writes to $HOME, so this runs the app with XDG paths
# redirected into the workspace and grabs the X11 desktop with gnome-screenshot,
# then crops to the app window rectangle reported by xwininfo.
#
# usage: capture-ui.sh <output.png> [--delay SECONDS] [--full] -- <app args...>
set -uo pipefail

WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1
mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME"

output="$1"; shift
delay=4
full=0
while [[ ${1:-} == --* ]]; do
  case "$1" in
    --delay) delay="$2"; shift 2 ;;
    --full) full=1; shift ;;
    --) shift; break ;;
    *) break ;;
  esac
done

app="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
log="$WORKSPACE_ROOT/.tools/out/capture-app.log"
mkdir -p "$WORKSPACE_ROOT/.tools/out"
: > "$log"

"$DOTNET_ROOT/dotnet" "$app" "$@" >>"$log" 2>&1 &
app_pid=$!
cleanup() { kill "$app_pid" 2>/dev/null; wait "$app_pid" 2>/dev/null; }
trap cleanup EXIT

sleep "$delay"
raw="$WORKSPACE_ROOT/.tools/out/.capture-raw.png"
rm -f "$raw"
gnome-screenshot -f "$raw" >/dev/null 2>&1

geometry=""
for _ in $(seq 1 20); do
  geometry=$(xwininfo -root -tree 2>/dev/null | grep -F 'Little Tools' | head -1)
  [[ -n "$geometry" ]] && break
  sleep 0.5
done

if [[ $full -eq 1 || -z "$geometry" ]]; then
  cp "$raw" "$output"
  echo "captured full desktop -> $output (window: ${geometry:-not found})"
else
  # xwininfo -tree prints: "<id> \"name\": (\"class\" \"Class\")  WxH+X+Y  +relx+rely"
  python3 - "$raw" "$output" "$geometry" <<'PY'
import re, sys
from PIL import Image
raw, out, line = sys.argv[1], sys.argv[2], sys.argv[3]
m = re.search(r'\s(\d+)x(\d+)\+(-?\d+)\+(-?\d+)\s+\+(-?\d+)\+(-?\d+)', line)
if not m:
    print("could not parse geometry:", line, file=sys.stderr)
    sys.exit(1)
w, h, x, y = (int(m.group(i)) for i in range(1, 5))
im = Image.open(raw).convert("RGB")
x2, y2 = max(0, x), max(0, y)
crop = im.crop((x2, y2, min(im.size[0], x + w), min(im.size[1], y + h)))
crop.save(out)
print(f"captured window {w}x{h}+{x}+{y} -> {out} ({crop.size[0]}x{crop.size[1]})")
PY
fi

#!/usr/bin/env bash
# Local test helper (dev-only): start/stop the workspace build of the suite with
# the stock module on the real X display so x11tool can drive it.
#
#   stockctl.sh start [--live]   start with fixture replay (default) or live data
#   stockctl.sh stop             stop it
#   stockctl.sh capsule          window id of the capsule
#   stockctl.sh details          window id of the detail window
#   stockctl.sh geom <id>        geometry of a window id
#   stockctl.sh state <id>       map state of a window id
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1
APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
LOG="$WORKSPACE_ROOT/.tools/out/stock-app.log"
PIDFILE="$WORKSPACE_ROOT/.tools/out/stock-app.pid"

win_id() { xwininfo -root -tree 2>/dev/null | grep -F "\"Little Tools · $1\":" | head -1 | awk '{print $1}'; }

case "${1:-}" in
  start)
    mode="${2:-fixtures}"
    mkdir -p "$WORKSPACE_ROOT/.tools/out"
    : > "$LOG"
    if [[ "$mode" == "--live" ]]; then unset LITTLETOOLS_STOCK_FIXTURES; else export LITTLETOOLS_STOCK_FIXTURES=1; fi
    setsid "$DOTNET_ROOT/dotnet" "$APP" --stock >>"$LOG" 2>&1 < /dev/null &
    for _ in $(seq 1 40); do
      sleep 0.5
      [[ -n "$(win_id 股票观察)" ]] && break
    done
    id="$(win_id 股票观察)"
    if [[ -z "$id" ]]; then echo "no capsule window"; cat "$LOG"; exit 1; fi
    pid="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | awk '{print $3}')"
    echo "$pid" > "$PIDFILE"
    echo "capsule=$id pid=$pid mode=$mode"
    ;;
  stop)
    # --exit goes through the suite's own command pipe.
    "$DOTNET_ROOT/dotnet" "$APP" --exit >/dev/null 2>&1 || true
    sleep 1
    if [[ -f "$PIDFILE" ]]; then kill "$(cat "$PIDFILE")" 2>/dev/null; fi
    sleep 1
    rm -f "$PIDFILE"
    echo stopped
    ;;
  capsule) win_id 股票观察 ;;
  details) win_id 股票观察明细 ;;
  geom) xwininfo -id "$2" 2>/dev/null | grep -E 'Absolute upper-left|Width:|Height:|Map State' ;;
  state) xwininfo -id "$2" 2>/dev/null | grep -E 'Map State' ;;
  *) echo "usage: $0 start [--live]|stop|capsule|details|geom <id>|state <id>"; exit 2 ;;
esac

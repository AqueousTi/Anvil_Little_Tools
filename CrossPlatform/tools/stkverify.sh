#!/usr/bin/env bash
# Local test helper (dev-only): run the *workspace* build of the suite in an
# isolated XDG root plus an isolated TMPDIR, so it neither touches the user's
# live install nor collides with it through the single-instance command pipe.
#
#   stkverify.sh start [--background|--stock|--todo ...]   launch and print pid
#   stkverify.sh stop                                      --exit through the pipe
#   stkverify.sh pid
#   stkverify.sh env                                       show the isolated root
#   stkverify.sh manager <json>                            write manager.json
#   stkverify.sh stock <json>                              write stock settings.json
#   stkverify.sh windows                                   list suite windows on :1
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"
ROOT="$WORKSPACE_ROOT/.tools/stkverify"
export XDG_CONFIG_HOME="$ROOT/config" XDG_DATA_HOME="$ROOT/data" XDG_CACHE_HOME="$ROOT/cache"
export TMPDIR="$ROOT/tmp"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1
APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
LOG="$ROOT/app.log"
PIDFILE="$ROOT/app.pid"

case "${1:-}" in
  start)
    shift || true
    mkdir -p "$XDG_CONFIG_HOME/little-tools" "$XDG_DATA_HOME/little-tools" "$XDG_CACHE_HOME" "$TMPDIR"
    : > "$LOG"
    setsid "$DOTNET_ROOT/dotnet" "$APP" "$@" >>"$LOG" 2>&1 < /dev/null &
    app_pid="$!"
    echo "$app_pid" > "$PIDFILE"
    for _ in $(seq 1 24); do
      sleep 0.5
      kill -0 "$app_pid" 2>/dev/null || break
      [[ -n "$(bash "$WORKSPACE_ROOT/CrossPlatform/tools/trayctl.sh" service "$app_pid" 2>/dev/null)" ]] && break
    done
    echo "$app_pid"
    ;;
  stop)
    "$DOTNET_ROOT/dotnet" "$APP" --exit >/dev/null 2>&1 || true
    sleep 1.5
    if [[ -f "$PIDFILE" ]]; then kill "$(cat "$PIDFILE")" 2>/dev/null || true; fi
    rm -f "$PIDFILE"
    echo stopped
    ;;
  pid) cat "$PIDFILE" 2>/dev/null ;;
  env)
    echo "XDG_CONFIG_HOME=$XDG_CONFIG_HOME"
    echo "TMPDIR=$TMPDIR"
    echo "manager=$XDG_CONFIG_HOME/little-tools/manager.json"
    echo "stock=$XDG_DATA_HOME/little-tools/stock/settings.json"
    ;;
  manager) mkdir -p "$(dirname "$XDG_CONFIG_HOME/little-tools/manager.json")"; printf '%s\n' "$2" > "$XDG_CONFIG_HOME/little-tools/manager.json" ;;
  stock)   mkdir -p "$(dirname "$XDG_DATA_HOME/little-tools/stock/settings.json")"; printf '%s\n' "$2" > "$XDG_DATA_HOME/little-tools/stock/settings.json" ;;
  windows) DISPLAY="$DISPLAY" xwininfo -root -tree 2>/dev/null | grep -F "Little Tools" ;;
  *) echo "usage: $0 start [args]|stop|pid|env|manager <json>|stock <json>|windows"; exit 2 ;;
esac

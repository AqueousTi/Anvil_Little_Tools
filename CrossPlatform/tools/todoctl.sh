#!/usr/bin/env bash
# Local test helper (dev-only): start/stop the workspace build of the todo app
# on the real X display so x11tool can drive it.
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1
export LITTLETOOLS_TODO_DATA="${LITTLETOOLS_TODO_DATA:-}"
# Keep the fcitx candidate window out of the screenshots while typing test items.
export XMODIFIERS="@im=none"
export GTK_IM_MODULE=gtk-im-context-simple
APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
LOG="$WORKSPACE_ROOT/.tools/out/todo-app.log"
PIDFILE="$WORKSPACE_ROOT/.tools/out/todo-app.pid"

win_id() { xwininfo -root -tree 2>/dev/null | grep -F 'Little Tools · Daily Todo' | grep -F 'dotnet' | head -1 | awk '{print $1}'; }

case "${1:-}" in
  start)
    mkdir -p "$WORKSPACE_ROOT/.tools/out"
    : > "$LOG"
    setsid "$DOTNET_ROOT/dotnet" "$APP" --todo >>"$LOG" 2>&1 < /dev/null &
    for _ in $(seq 1 40); do
      sleep 0.5
      id="$(win_id)"
      [[ -n "$id" ]] && break
    done
    id="$(win_id)"
    if [[ -z "$id" ]]; then echo "no window"; cat "$LOG"; exit 1; fi
    pid="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | awk '{print $3}')"
    echo "$pid" > "$PIDFILE"
    echo "window=$id pid=$pid"
    ;;
  stop)
    if [[ -f "$PIDFILE" ]]; then kill "$(cat "$PIDFILE")" 2>/dev/null; fi
    sleep 1
    rm -f "$PIDFILE"
    echo stopped
    ;;
  pid)
    cat "$PIDFILE" 2>/dev/null
    ;;
  geom)
    xwininfo -id "$(win_id)" 2>/dev/null | grep -E 'Absolute upper-left|Width|Height'
    ;;
  *) echo "usage: $0 start|stop|geom"; exit 2 ;;
esac

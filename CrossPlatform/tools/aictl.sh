#!/usr/bin/env bash
# Local test helper (dev-only): drive an ISOLATED workspace build of the
# assistant on the real X display so a hide/show cycle can be observed with
# xprop. Isolation is by TMPDIR (dotnet's named pipes and named mutexes live
# under TMPDIR on Unix, so the installed suite and this instance cannot steal
# each other's single-instance pipe) plus isolated XDG directories.
#
#   aictl.sh start [app args...]   start the instance (default: translation)
#   aictl.sh send [app args...]    send a command through the private pipe
#   aictl.sh stop                  stop it (--exit through the private pipe)
#   aictl.sh id                    window id of the assistant window
#   aictl.sh state [id]            _NET_WM_STATE of the assistant window
#   aictl.sh map [id]              map state of the assistant window
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"
ISO="$WORKSPACE_ROOT/.tools/tmp/ai-iso"
mkdir -p "$ISO"
export TMPDIR="$ISO"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1
APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
LOG="$WORKSPACE_ROOT/.tools/out/ai-app.log"
PIDFILE="$ISO/app.pid"

# Several Little Tools instances share this display (the installed suite runs in
# the background), so the window is picked by the PID recorded at start instead
# of by title alone.
win_id() {
  local want="" id pid line
  [[ -f "$PIDFILE" ]] && want="$(cat "$PIDFILE")"
  while read -r line; do
    id="$(echo "$line" | awk '{print $1}')"
    pid="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | awk '{print $3}')"
    if [[ -z "$want" || "$pid" == "$want" ]]; then echo "$id"; return; fi
  done < <(xwininfo -root -tree 2>/dev/null | grep -F '"Little Tools AI"')
}

case "${1:-}" in
  start)
    shift || true
    mkdir -p "$WORKSPACE_ROOT/.tools/out"
    : > "$LOG"
    setsid "$DOTNET_ROOT/dotnet" "$APP" "$@" >>"$LOG" 2>&1 < /dev/null &
    echo "$!" > "$PIDFILE"
    # The X window exists before Avalonia reaches Show(), so wait for it to be
    # mapped: a probe taken any earlier reads a half-initialised window.
    for _ in $(seq 1 60); do
      sleep 0.25
      id="$(win_id)"
      [[ -n "$id" && "$(xwininfo -id "$id" 2>/dev/null | sed -n 's/.*Map State: *//p')" == "IsViewable" ]] && break
    done
    id="$(win_id)"
    if [[ -z "$id" ]]; then echo "no window"; cat "$LOG"; exit 1; fi
    pid="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | awk '{print $3}')"
    echo "window=$id pid=$pid"
    ;;
  send)
    shift || true
    "$DOTNET_ROOT/dotnet" "$APP" "$@" >>"$LOG" 2>&1
    ;;
  stop)
    "$DOTNET_ROOT/dotnet" "$APP" --exit >/dev/null 2>&1 || true
    sleep 1
    if [[ -f "$PIDFILE" ]]; then kill "$(cat "$PIDFILE")" 2>/dev/null; fi
    sleep 0.5
    rm -f "$PIDFILE"
    echo stopped
    ;;
  id) win_id ;;
  state)
    target="${2:-$(win_id)}"
    xprop -id "$target" _NET_WM_STATE 2>/dev/null | sed 's/^_NET_WM_STATE[^=]*= *//'
    ;;
  map)
    target="${2:-$(win_id)}"
    xwininfo -id "$target" 2>/dev/null | sed -n 's/.*Map State: *//p'
    ;;
  geom)
    target="${2:-$(win_id)}"
    xwininfo -id "$target" 2>/dev/null | grep -E 'Absolute upper-left|Width:|Height:'
    ;;
  *) echo "usage: $0 start|send|stop|id|state|map|geom [args]"; exit 2 ;;
esac

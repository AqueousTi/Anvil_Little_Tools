#!/usr/bin/env bash
# Local test helper (dev-only): prove that the assistant window keeps
# _NET_WM_STATE_SKIP_TASKBAR across repeated show -> click-away hide -> show
# cycles, for both entry points (translation and chat).
#
#   ai-flags-test.sh <translate|chat> [rounds]
#
# Every step prints the _NET_WM_STATE of the window owned by the isolated
# instance started with aictl.sh.
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export DISPLAY="${DISPLAY:-:1}"
CTL="$WORKSPACE_ROOT/CrossPlatform/tools/aictl.sh"
entry="${1:-translate}"
rounds="${2:-3}"
case "$entry" in
  translate) cmd=() ;;
  chat) cmd=(--chat) ;;
  *) echo "usage: $0 <translate|chat> [rounds]"; exit 2 ;;
esac

window_state() { xprop -id "$1" _NET_WM_STATE 2>/dev/null | sed 's/^_NET_WM_STATE[^=]*= *//'; }
map_state() { xwininfo -id "$1" 2>/dev/null | sed -n 's/.*Map State: *//p'; }
active() { xprop -id "$1" _NET_WM_STATE 2>/dev/null | grep -q _NET_WM_STATE_FOCUSED && echo yes || echo no; }

show_and_wait() {
  "$CTL" send "${cmd[@]}" >/dev/null 2>&1
  for _ in $(seq 1 30); do
    sleep 0.2
    [[ "$(map_state "$1")" == "IsViewable" ]] && return 0
  done
  return 1
}

hide_by_click() {
  for _ in $(seq 1 10); do
    "$WORKSPACE_ROOT/.tools/x11tool" click 200 300 >/dev/null 2>&1
    for _ in $(seq 1 15); do
      sleep 0.2
      [[ "$(map_state "$1")" == "IsUnMapped" ]] && return 0
    done
  done
  return 1
}

id="$("$CTL" id)"
if [[ -z "$id" ]]; then echo "no isolated assistant window (start it with aictl.sh start)"; exit 1; fi
echo "entry=$entry window=$id pid=$(cat "$WORKSPACE_ROOT/.tools/tmp/ai-iso/app.pid" 2>/dev/null) rounds=$rounds"
fail=0
for round in $(seq 1 "$rounds"); do
  if show_and_wait "$id"; then
    state="$(window_state "$id")"
    printf 'round %d show : map=%-10s active=%-3s state=%s\n' "$round" "$(map_state "$id")" "$(active "$id")" "$state"
    [[ "$state" == *SKIP_TASKBAR* ]] || { echo "  FAIL: SKIP_TASKBAR missing after show"; fail=1; }
  else
    printf 'round %d show : FAILED to become visible (state=%s)\n' "$round" "$(window_state "$id")"
    fail=1
  fi
  if hide_by_click "$id"; then
    printf 'round %d hide : map=%-10s state=%s\n' "$round" "$(map_state "$id")" "$(window_state "$id")"
  else
    printf 'round %d hide : FAILED to hide by click-away (map=%s state=%s)\n' "$round" "$(map_state "$id")" "$(window_state "$id")"
    fail=1
  fi
done
echo "result=$([[ $fail -eq 0 ]] && echo PASS || echo FAIL)"
exit "$fail"

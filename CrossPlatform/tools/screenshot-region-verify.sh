#!/usr/bin/env bash
# One-off repro/verification harness for the screenshot-translation capture path.
#
# It drives a real X display and asserts the behaviour the fix is about:
#   1. an entry point (CLI / global hotkey / tray) makes the selection overlay
#      appear as a full-monitor window owned by the app;
#   2. a real pointer drag (XTest) is accepted, the overlay closes, and the
#      assistant window comes back expanded instead of staying hidden;
#   3. (with GTK_ENTRY=esc) Esc cancels and the window still comes back.
#
# Isolation is by short-path TMPDIR (dotnet's named pipes/mutexes live under
# TMPDIR, so the installed suite cannot steal the single-instance pipe) plus
# isolated XDG directories, exactly like aictl.sh.
#
#   screenshot-region-verify.sh [outdir] [entry] [x1 y1 x2 y2]
#     entry: cli (--screenshot, default) | hotkey (--background + Ctrl+Alt+X) | tray
#
# The hotkey entry needs the four global grabs to be free: stop any other
# Little Tools instance first (e.g.  little-tools --exit  or the installed
# /home/<user>/.local/share/little-tools/app/LittleTools.Assistant --exit).
#
# Known desktop limit: GNOME draws its 32px panel above any *managed* window, so
# that strip stays unselectable. The overlay is deliberately managed (not
# override-redirect) because it has to receive the Esc key.
set -uo pipefail

WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"

OUTDIR="${1:-$WORKSPACE_ROOT/.tools/out/screenshot-region}"
ENTRY="${2:-cli}"
read -r X1 Y1 X2 Y2 <<<"${3:-600} ${4:-400} ${5:-1400} ${6:-800}"

APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
X11TOOL="$WORKSPACE_ROOT/.tools/x11tool"
ISO="/tmp/lt-shot-verify"
export TMPDIR="$ISO"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1

mkdir -p "$OUTDIR" "$ISO"
LOG="$OUTDIR/app.log"
: > "$LOG"
pass=0; fail=0
ok()   { echo "PASS  $1"; pass=$((pass + 1)); }
bad()  { echo "FAIL  $1"; fail=$((fail + 1)); }

# Every window owned by the app pid: "<id> <w>x<h>+<x>+<y> <MapState>".
app_windows() {
  local want="$1" id pid
  for id in $(xwininfo -root -children 2>/dev/null | awk '/^     0x/{print $1}'); do
    pid="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | awk '{print $3}')"
    [[ "$pid" == "$want" ]] || continue
    xwininfo -id "$id" 2>/dev/null | awk -v id="$id" '
      /Absolute upper-left X/{x=$4} /Absolute upper-left Y/{y=$4}
      /Width:/{w=$2} /Height:/{h=$2} /Map State:/{m=$3}
      END{print id" "w"x"h"+"x"+"y" "m}'
  done
}

# The overlay is the only app window as large as a whole monitor.
monitor_sizes() {
  xrandr --listmonitors 2>/dev/null | awk '/^ [0-9]+: /{print $3}' \
    | sed -E 's|^([0-9]+)/[0-9]+x([0-9]+)/[0-9]+.*|\1x\2|'
}

overlay_of() {
  local pid="$1" id geo state size sizes
  sizes="$(monitor_sizes)"
  app_windows "$pid" | while read -r id geo state; do
    for size in $sizes; do
      if [[ "$geo" == "$size"+* ]]; then echo "$id $geo $state"; break; fi
    done
  done
}

stop_app() {
  [[ -n "${APP_PID:-}" ]] || return 0
  kill "$APP_PID" 2>/dev/null
  sleep 1
  kill -9 "$APP_PID" 2>/dev/null
  wait "$APP_PID" 2>/dev/null
}

trap stop_app EXIT

case "$ENTRY" in
  cli)    START_ARGS=(--screenshot) ;;
  hotkey) START_ARGS=(--background) ;;
  tray)   START_ARGS=(--background) ;;
  *) echo "unknown entry: $ENTRY (cli|hotkey|tray)"; exit 2 ;;
esac

# Only the global-hotkey entry competes for the X key grabs; the tray entry goes
# through StatusNotifierItem and works while another instance owns them.
if [[ "$ENTRY" == "hotkey" ]]; then
  if ! "$X11TOOL" grab-check | grep -q "Ctrl+Alt+X *ok=4"; then
    echo "the global hotkey grabs are already taken by another instance; run 'little-tools --exit' first" >&2
    "$X11TOOL" grab-check >&2
    exit 3
  fi
fi

setsid "$DOTNET_ROOT/dotnet" "$APP" "${START_ARGS[@]}" >>"$LOG" 2>&1 </dev/null &
APP_PID=$!

# The app window exists before Avalonia reaches Show(); wait for the process to
# own a window at all, then for the overlay.
APP_WIN=""
for _ in $(seq 1 80); do
  sleep 0.25
  APP_WIN="$(app_windows "$APP_PID" | awk '{print $1; exit}')"
  [[ -n "$APP_WIN" ]] && break
done
if [[ -z "$APP_WIN" ]]; then echo "no app window; log:"; cat "$LOG"; exit 1; fi
echo "app pid=$APP_PID first window=$APP_WIN"

# A --background launch owns a window long before it has registered the global
# grabs / exported the tray menu, so wait for the entry point to be ready before
# triggering it (an early trigger is silently dropped).
case "$ENTRY" in
  hotkey)
    for _ in $(seq 1 40); do
      "$X11TOOL" grab-check | grep -q "Ctrl+Alt+X *ok=0" && break
      sleep 0.25
    done
    ;;
  tray)
    for _ in $(seq 1 40); do
      bash "$WORKSPACE_ROOT/CrossPlatform/tools/trayctl.sh" items "$APP_PID" 2>/dev/null | grep -q "截图翻译" && break
      sleep 0.25
    done
    ;;
esac

case "$ENTRY" in
  cli)    ;;
  hotkey) "$X11TOOL" send x ctrl+alt ;;
  tray)   bash "$WORKSPACE_ROOT/CrossPlatform/tools/trayctl.sh" click "$APP_PID" "截图翻译" >/dev/null ;;
esac

OVERLAY=""
for _ in $(seq 1 40); do
  sleep 0.25
  OVERLAY="$(overlay_of "$APP_PID" | awk '{print $1}')"
  [[ -n "$OVERLAY" ]] && break
done
if [[ -n "$OVERLAY" ]]; then
  ok "$ENTRY entry shows the selection overlay ($OVERLAY)"
else
  bad "$ENTRY entry did not show the selection overlay"
  echo "--- app windows ---"; app_windows "$APP_PID"
  echo "--- log ---"; cat "$LOG"
  exit 1
fi

# The overlay must cover one whole monitor (monitored sizes come from xrandr).
OVERLAY_GEO="$(overlay_of "$APP_PID" | awk '{print $2}')"
echo "overlay geometry $OVERLAY_GEO; monitors: $(monitor_sizes | tr '\n' ' ')"
if [[ "$OVERLAY_GEO" == *"x"* ]]; then
  ok "overlay covers the whole monitor ($OVERLAY_GEO)"
else
  bad "could not read the overlay geometry"
fi

gnome-screenshot -f "$OUTDIR/overlay.png" >/dev/null 2>&1 \
  && ok "captured the live overlay to overlay.png" \
  || bad "could not capture the overlay"

if [[ "${GTK_ENTRY:-drag}" == "esc" ]]; then
  "$X11TOOL" move "$X1" "$Y1" >/dev/null
  "$X11TOOL" send Escape "" >/dev/null
else
  "$X11TOOL" move "$X1" "$Y1" >/dev/null
  "$X11TOOL" drag "$X1" "$Y1" "$X2" "$Y2" 0 >/dev/null
fi
sleep 2

if [[ -z "$(overlay_of "$APP_PID")" ]]; then
  ok "overlay closed after the selection"
else
  bad "overlay is still up after the selection"
fi

# After a drag (or Esc) the window must be visible again; the old bug left it
# hidden forever behind a hung gnome-screenshot -a.
MAIN="$(app_windows "$APP_PID" | awk -v o="${OVERLAY:-}" '$1 != o && $3 == "IsViewable" {print $1" "$2; exit}')"
if [[ -n "$MAIN" ]]; then
  ok "assistant window is visible again ($MAIN)"
else
  bad "assistant window did not come back"
fi

bash "$WORKSPACE_ROOT/CrossPlatform/tools/shotwin.sh" "$APP_PID" "Little Tools AI" "$OUTDIR/after-selection.png" >/dev/null 2>&1 \
  && ok "captured the assistant window to after-selection.png" \
  || bad "could not capture the assistant window"

echo
echo "evidence: $OUTDIR/overlay.png  $OUTDIR/after-selection.png  $OUTDIR/app.log"
echo "entry=$ENTRY drag=${X1},${Y1}->${X2},${Y2} pass=$pass fail=$fail"
[[ "$fail" == 0 ]]

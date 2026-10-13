#!/usr/bin/env bash
# Reproduce and verify the Linux logout fix: a user close of a hide-on-close
# window keeps hiding (the Windows behaviour), but a close request that arrives
# while the desktop session is going away quits the process instead.
#
#   session-logout-verify.sh [outdir]
#
# Four ways to get the answer, in increasing order of "real":
#
#   1. real session bus              -> the user close still hides
#   2. LITTLETOOLS_FORCE_SESSION_ENDING/.._ACTIVE (test only)
#                                    -> both branches of the decision, no logout
#   3. private dbus-daemon + fake-session-manager.py with SessionIsActive=false
#                                    -> the *real* D-Bus link makes the close quit
#   4. the same stand-in emits SessionOver
#                                    -> the app leaves without any close request
#
# It never logs the user out: the private bus only exists for these instances.
#
# Requires: the workspace Release build, .tools/wmdelete (see tools/README.md),
# xwininfo/xprop/gdbus, dbus-daemon and python3-gi.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/CrossPlatform/tools/env.sh"
OUT="${1:-$ROOT/.tools/out/session-logout-verify}"
mkdir -p "$OUT"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1

APP="$ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
WMDELETE="$ROOT/.tools/wmdelete"
FAKE="$ROOT/CrossPlatform/tools/fake-session-manager.py"

# Isolated XDG roots and a *short* TMPDIR: the suite's single-instance pipe lives
# at $TMPDIR/.corefxpipe_*, and an AF_UNIX path is limited to ~108 characters, so
# the TMPDIR must not live under the (already long) workspace path. Isolation is
# what keeps these instances away from a running installed suite.
export XDG_CONFIG_HOME="$OUT/xdg/config" XDG_DATA_HOME="$OUT/xdg/data" XDG_CACHE_HOME="$OUT/xdg/cache"
export TMPDIR="/tmp/lt-session-verify"
mkdir -p "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME" "$TMPDIR"
unset LITTLETOOLS_FORCE_SESSION_ENDING LITTLETOOLS_FORCE_SESSION_ACTIVE 2>/dev/null || true

# Resolving a stored secret shells out to `secret-tool`, and libsecret blocks
# forever on a bus without org.freedesktop.secrets (which is exactly what the
# private bus below is). That is unrelated to this harness - the isolated XDG
# roots have no credentials to find anyway - so the documented seam is used to
# keep the app's main thread out of the keyring.
export LITTLETOOLS_DISABLE_KEYRING=1

pass=0; fail=0
ok() { pass=$((pass + 1)); echo "  PASS $1"; }
no() { fail=$((fail + 1)); echo "  FAIL $1"; }
expect() { if [[ "$2" == "$3" ]]; then ok "$1 ($3)"; else no "$1 (expected $3, got $2)"; fi; }

app_job=""; app_pid=""; app_wid=""; last_status=""
dbus_addr=""; dbus_pid=""; fake_pid=""

cleanup() {
  [[ -n "$app_pid" ]] && kill -9 "$app_pid" 2>/dev/null
  [[ -n "$fake_pid" ]] && kill "$fake_pid" 2>/dev/null
  [[ -n "$dbus_pid" ]] && kill "$dbus_pid" 2>/dev/null
  return 0
}
trap cleanup EXIT

window_of() { # window_of <pid> [title substring]
  local pid="$1" title="${2:-Little Tools AI}" id wpid
  for id in $(xwininfo -root -tree 2>/dev/null | grep -F "\"$title\"" | awk '{print $1}'); do
    wpid="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | awk '{print $3}')"
    [[ "$wpid" == "$pid" ]] && { echo "$id"; return; }
  done
}

map_state() { xwininfo -id "$1" 2>/dev/null | sed -n 's/.*Map State: *//p'; }

start_instance() { # start_instance <label> [app args...]
  local label="$1"; shift
  local log="$OUT/$label.log" pidfile="$OUT/$label.pid"
  : > "$log"; rm -f "$pidfile"
  bash -c 'echo $$ > "$1"; shift; exec "$@"' _ "$pidfile" "$DOTNET_ROOT/dotnet" "$APP" "$@" \
    >>"$log" 2>&1 </dev/null &
  app_job=$!
  app_pid=""; app_wid=""
  local _
  for _ in $(seq 1 80); do
    sleep 0.25
    [[ -f "$pidfile" ]] || continue
    app_pid="$(cat "$pidfile")"
    kill -0 "$app_pid" 2>/dev/null || break
    app_wid="$(window_of "$app_pid")"
    # The window exists before Avalonia reaches Show(), so a probe taken any
    # earlier reads a half-initialised (unmapped) window.
    [[ -n "$app_wid" && "$(map_state "$app_wid")" == "IsViewable" ]] && return 0
  done
  return 1
}

# Waits up to <limit> tenths of a second. On success last_status carries the exit code.
wait_for_exit() {
  local limit="$1" i
  for ((i = 0; i < limit; i++)); do
    if ! kill -0 "$app_pid" 2>/dev/null; then
      wait "$app_job" 2>/dev/null
      last_status=$?
      return 0
    fi
    sleep 0.1
  done
  return 1
}

stop_instance() {
  [[ -z "$app_pid" ]] && return 0
  kill -TERM "$app_pid" 2>/dev/null
  local _
  for _ in $(seq 1 30); do kill -0 "$app_pid" 2>/dev/null || break; sleep 0.1; done
  kill -9 "$app_pid" 2>/dev/null
  wait "$app_job" 2>/dev/null
  app_pid=""; app_wid=""
}

now_ms() { echo $(( $(date +%s%N) / 1000000 )); }

echo "== session logout verification (outdir $OUT)"

# ---------------------------------------------------------------- 1. user close
echo "-- 1. a user close still hides (real session bus)"
if start_instance user-close; then
  expect "assistant window is viewable" "$(map_state "$app_wid")" "IsViewable"
  "$WMDELETE" "$app_wid" >/dev/null
  sleep 1.5
  if kill -0 "$app_pid" 2>/dev/null; then ok "process survived the close request"; else no "process died on a user close"; fi
  expect "window was hidden" "$(map_state "$app_wid")" "IsUnMapped"
else
  no "the user-close instance did not start"
fi
stop_instance

# ------------------------------------------------- 2. forced session-ending switch
echo "-- 2. forced 'session is ending' quits on the close request"
export LITTLETOOLS_FORCE_SESSION_ENDING=1
if start_instance forced-ending; then
  if kill -0 "$app_pid" 2>/dev/null; then ok "the forced switch does not quit at startup"; else no "the app quit at startup"; fi
  started=$(now_ms)
  "$WMDELETE" "$app_wid" >/dev/null
  if wait_for_exit 100; then
    elapsed=$(( $(now_ms) - started ))
    ok "process exited ${elapsed}ms after the close request (exit code $last_status)"
    if (( elapsed < 3000 )); then ok "the exit fits inside the desktop's logout wait"; else no "the exit took ${elapsed}ms"; fi
  else
    no "process still alive 10s after the close request"
  fi
else
  no "the forced-ending instance did not start"
fi
stop_instance
unset LITTLETOOLS_FORCE_SESSION_ENDING

echo "-- 3. forced 'session is running' still hides"
export LITTLETOOLS_FORCE_SESSION_ACTIVE=1
if start_instance forced-active; then
  "$WMDELETE" "$app_wid" >/dev/null
  sleep 1.5
  if kill -0 "$app_pid" 2>/dev/null; then ok "process survived"; else no "process died with the active switch"; fi
  expect "window was hidden" "$(map_state "$app_wid")" "IsUnMapped"
else
  no "the forced-active instance did not start"
fi
stop_instance
unset LITTLETOOLS_FORCE_SESSION_ACTIVE

# ------------------------------------- 4. the other hide-on-close module windows
echo "-- 4. the stock capsule and the monitor HUD apply the same rule"
# Turn every module on so the isolated instance really owns those windows.
mkdir -p "$XDG_CONFIG_HOME/little-tools"
printf '%s\n' '{"MonitorEnabled":true,"TranslateEnabled":true,"TodoNotesEnabled":true,"StockEnabled":true,"EdgeHideMonitor":false,"EdgeHideTranslate":false,"EdgeHideTodo":false,"EdgeHideStock":false}' \
  > "$XDG_CONFIG_HOME/little-tools/manager.json"

if start_instance modules; then
  stock_wid="$(window_of "$app_pid" "Little Tools · 股票观察")"
  monitor_wid="$(window_of "$app_pid" "Little Tools · AI 余量监控")"
  if [[ -z "$stock_wid" || -z "$monitor_wid" ]]; then
    no "the module windows did not appear (stock=$stock_wid monitor=$monitor_wid)"
  else
    ok "the stock capsule and the monitor HUD are on screen"
    "$WMDELETE" "$stock_wid" >/dev/null
    sleep 1.5
    if kill -0 "$app_pid" 2>/dev/null; then ok "the stock capsule close only hid it"; else no "the stock capsule close killed the process"; fi
    expect "stock capsule was hidden" "$(map_state "$stock_wid")" "IsUnMapped"
    "$WMDELETE" "$monitor_wid" >/dev/null
    sleep 1.5
    if kill -0 "$app_pid" 2>/dev/null; then ok "the monitor HUD close only hid it"; else no "the monitor HUD close killed the process"; fi
    expect "monitor HUD was hidden" "$(map_state "$monitor_wid")" "IsUnMapped"
  fi
else
  no "the module instance did not start"
fi
stop_instance

export LITTLETOOLS_FORCE_SESSION_ENDING=1
if start_instance modules-ending; then
  stock_wid="$(window_of "$app_pid" "Little Tools · 股票观察")"
  if [[ -z "$stock_wid" ]]; then
    no "the stock capsule did not appear"
  else
    started=$(now_ms)
    "$WMDELETE" "$stock_wid" >/dev/null
    if wait_for_exit 100; then
      elapsed=$(( $(now_ms) - started ))
      ok "a session-ending close request on the stock capsule quit the process (${elapsed}ms, exit code $last_status)"
    else
      no "process still alive 10s after the stock capsule close request"
    fi
  fi
else
  no "the module-ending instance did not start"
fi
stop_instance
unset LITTLETOOLS_FORCE_SESSION_ENDING

# ------------------------------------- 5./6. real D-Bus link to a stand-in service
echo "-- 5. real bus: a stand-in SessionManager with SessionIsActive=false ends the process"
mapfile -t dbus_info < <(dbus-daemon --session --fork --print-address=1 --print-pid=1 2>/dev/null)
dbus_addr="${dbus_info[0]:-}"; dbus_pid="${dbus_info[1]:-}"
if [[ -z "$dbus_addr" ]]; then
  no "could not start a private session bus"
else
  export DBUS_SESSION_BUS_ADDRESS="$dbus_addr"
  fake_log="$OUT/fake-session-manager.log"; : > "$fake_log"
  python3 "$FAKE" --active true --log "$fake_log" >"$OUT/fake-session-manager.out" 2>&1 &
  fake_pid=$!
  owned=false
  for _ in $(seq 1 40); do
    sleep 0.25
    if gdbus call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus \
        --method org.freedesktop.DBus.NameHasOwner org.gnome.SessionManager 2>/dev/null | grep -q true; then owned=true; break; fi
  done
  if [[ "$owned" != true ]]; then
    no "the stand-in SessionManager did not take the name"
  elif start_instance real-bus-active; then
    if kill -0 "$app_pid" 2>/dev/null; then ok "the app runs against the stand-in session manager"; else no "the app quit at startup"; fi
    "$WMDELETE" "$app_wid" >/dev/null
    sleep 1.5
    if grep -q "get SessionIsActive -> true" "$fake_log"; then ok "the app read SessionIsActive over the real bus"; else no "no SessionIsActive read reached the stand-in"; fi
    if kill -0 "$app_pid" 2>/dev/null; then ok "process survived while that property was true"; else no "process died while the session was active"; fi
    expect "window was hidden" "$(map_state "$app_wid")" "IsUnMapped"
  else
    no "the real-bus (active) instance did not start"
  fi
  stop_instance

  gdbus call --session --dest org.gnome.SessionManager --object-path /org/gnome/SessionManager \
    --method org.gnome.SessionManager.SetSessionActive false >/dev/null 2>&1
  if grep -q "properties-changed SessionIsActive -> false" "$fake_log"; then ok "the stand-in announced the session ending"; else no "SetSessionActive left no trace"; fi

  if start_instance real-bus-ending; then
    if kill -0 "$app_pid" 2>/dev/null; then ok "an app started while that property was false stays up"; else no "the app quit at startup"; fi
    started=$(now_ms)
    "$WMDELETE" "$app_wid" >/dev/null
    if wait_for_exit 100; then
      elapsed=$(( $(now_ms) - started ))
      if grep -q "get SessionIsActive -> false" "$fake_log"; then ok "the app read the false property over the real bus"; else no "the close request did not read the property"; fi
      ok "process exited ${elapsed}ms after the close request (exit code $last_status)"
      if (( elapsed < 3000 )); then ok "the exit fits inside the desktop's logout wait"; else no "the exit took ${elapsed}ms"; fi
    else
      no "process still alive 10s after the session-ending close request"
    fi
  else
    no "the real-bus (ending) instance did not start"
  fi
  stop_instance

  echo "-- 6. real bus: SessionOver ends the process without any close request"
  if start_instance real-bus-over; then
    if kill -0 "$app_pid" 2>/dev/null; then ok "the SessionOver instance is running"; else no "the SessionOver instance quit at startup"; fi
    started=$(now_ms)
    gdbus call --session --dest org.gnome.SessionManager --object-path /org/gnome/SessionManager \
      --method org.gnome.SessionManager.EmitSessionOver >/dev/null 2>&1
    if wait_for_exit 100; then
      elapsed=$(( $(now_ms) - started ))
      if grep -q "signal SessionOver" "$fake_log"; then ok "the stand-in emitted SessionOver"; else no "no SessionOver reached the bus"; fi
      ok "process exited ${elapsed}ms after SessionOver (exit code $last_status)"
    else
      no "process still alive 10s after SessionOver"
    fi
  else
    no "the SessionOver instance did not start"
  fi
  stop_instance
  unset DBUS_SESSION_BUS_ADDRESS
fi

echo "== session logout verification: pass=$pass fail=$fail"
[[ "$fail" -eq 0 ]]

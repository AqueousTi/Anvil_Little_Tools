#!/usr/bin/env bash
# One-shot harness for the single-instance *version takeover*: it starts two
# copies of the workspace Release build with different injected build identities
# (LITTLETOOLS_BUILD_VERSION), on an isolated TMPDIR, and asserts what the
# coordinator does with each combination.
#
#   takeover-verify.sh [outdir]     default: .tools/out/takeover-verify
#
# It needs the same short-path TMPDIR rule as the other harnesses (AF_UNIX paths
# are limited to ~108 characters) and never touches the real XDG or a real
# installed instance, because the command pipe and the identity file both live in
# TMPDIR. Isolation also means the run cannot collide with the suite the user is
# actually running.
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"
OUT="${1:-$WORKSPACE_ROOT/.tools/out/takeover-verify}"
TMP="${LITTLETOOLS_TAKEOVER_TMPDIR:-/tmp/lt-takeover-verify}"
export TMPDIR="$TMP"
export XDG_CONFIG_HOME="$OUT/xdg/config" XDG_DATA_HOME="$OUT/xdg/data" XDG_CACHE_HOME="$OUT/xdg/cache"
export DISPLAY="${DISPLAY:-:1}"
APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
PIPE="$TMPDIR/CoreFxPipe_LittleTools.Assistant.Command.v1"
IDENTITY="$TMPDIR/little-tools-assistant.$USER.identity.json"
TAKEOVER="$TMPDIR/little-tools-assistant.$USER.takeover.json"

# A previous failed run may have left a copy holding this TMPDIR's pipe; stop it
# before wiping the directory, or the next bind would happen next to a live socket.
if [[ -f "$IDENTITY" ]]; then
  LEFTOVER="$(python3 -c "import json;print(json.load(open('$IDENTITY')).get('Pid',''))" 2>/dev/null)"
  if [[ -n "$LEFTOVER" ]]; then kill "$LEFTOVER" 2>/dev/null; sleep 2; fi
fi
rm -rf "$OUT" "$TMP"
mkdir -p "$OUT" "$TMP" "$XDG_CONFIG_HOME/little-tools"
# Only the translation module, so the run opens no module windows.
printf '%s\n' '{"MonitorEnabled":false,"TranslateEnabled":true,"TodoNotesEnabled":false,"StockEnabled":false,"EdgeHideMonitor":false,"EdgeHideTranslate":false,"EdgeHideTodo":false,"EdgeHideStock":false}' > "$XDG_CONFIG_HOME/little-tools/manager.json"

failures=0
pass() { echo "PASS $1: $2"; }
fail() { echo "FAIL $1: $2"; failures=$((failures + 1)); }
expect_eq() { if [[ "$2" == "$3" ]]; then pass "$1" "$3"; else fail "$1" "expected=$2 actual=$3"; fi; }

launch() { # version extra-args...
  local version="$1"; shift
  env LITTLETOOLS_BUILD_VERSION="$version" setsid "$DOTNET_ROOT/dotnet" "$APP" "$@" \
    >> "$OUT/app.log" 2>&1 < /dev/null &
}
identity_field() { # name
  python3 - "$IDENTITY" "$1" 2>/dev/null <<'PY'
import json, sys
try:
    print(json.load(open(sys.argv[1])).get(sys.argv[2], ""))
except Exception:
    pass
PY
}
takeover_field() { # name
  python3 - "$TAKEOVER" "$1" 2>/dev/null <<'PY'
import json, sys
try:
    print(json.load(open(sys.argv[1])).get(sys.argv[2], ""))
except Exception:
    pass
PY
}
alive() { [[ -n "${1:-}" ]] && kill -0 "$1" 2>/dev/null; }
# The user's installed suite may be on the same display, so a window only counts
# when _NET_WM_PID names the process under test.
window_for_pid() { # pid title
  local id owner
  for id in $(xwininfo -root -tree 2>/dev/null | grep -F "$2" | grep -o '0x[0-9a-f]*'); do
    owner="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | awk '{print $NF}')"
    if [[ "$owner" == "$1" ]]; then echo "$id"; return 0; fi
  done
  return 1
}
wait_for_identity() { # seconds [must-differ-from-pid]
  local pid
  for _ in $(seq 1 $(( $1 * 4 ))); do
    pid="$(identity_field Pid)"
    # A dead copy's record can still be on disk for a moment, so the pid must name
    # a live process (and, when asked, a different one than the copy being replaced).
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null && [[ "$pid" != "${2:-}" ]]; then return 0; fi
    sleep 0.25
  done
  return 1
}
dump_state() { # label
  echo "STATE ($1): identity=$(identity_field Pid)/$(identity_field Version) takeover=$(takeover_field FromPid)/$(takeover_field FromVersion)"
  ps -eo pid,cmd | grep "net10.0/LittleTools" | grep -v grep
  tail -5 "$OUT/app.log" 2>/dev/null
}

echo "== takeover-verify: $OUT (TMPDIR=$TMPDIR) =="
rm -f "$IDENTITY" "$TAKEOVER"

# ------------------------------------------------ 1. a different build takes over
launch old-build-111 --background
wait_for_identity 20 || { echo "FAIL start: no identity"; dump_state start; exit 1; }
OLD_PID="$(identity_field Pid)"
OLD_VERSION="$(identity_field Version)"
sleep 1
OLD_WINDOW="$(window_for_pid "$OLD_PID" 'Little Tools AI' || true)"
echo "old instance: pid=$OLD_PID version=$OLD_VERSION window=$OLD_WINDOW"
expect_eq "old.version" "old-build-111" "$OLD_VERSION"

launch new-build-222 --background
for _ in $(seq 1 40); do alive "$OLD_PID" || break; sleep 0.25; done
if alive "$OLD_PID"; then fail "takeover.old-exited" "old pid $OLD_PID is still running"; else pass "takeover.old-exited" "old pid $OLD_PID left"; fi
if ! wait_for_identity 20 "$OLD_PID"; then
  fail "takeover.new-primary" "the new build never published itself as primary"
  dump_state takeover
  echo "== $failures failures =="
  exit "$failures"
fi
NEW_PID="$(identity_field Pid)"
NEW_VERSION="$(identity_field Version)"
for _ in $(seq 1 40); do NEW_WINDOW="$(window_for_pid "$NEW_PID" 'Little Tools AI' || true)"; [[ -n "$NEW_WINDOW" ]] && break; sleep 0.25; done
echo "new instance: pid=$NEW_PID version=$NEW_VERSION window=$NEW_WINDOW"
expect_eq "takeover.new-version" "new-build-222" "$NEW_VERSION"
if [[ "$NEW_PID" != "$OLD_PID" ]]; then pass "takeover.pid-changed" "$OLD_PID -> $NEW_PID"; else fail "takeover.pid-changed" "still $OLD_PID"; fi
if [[ -n "$NEW_WINDOW" ]]; then
  OWNER="$(xprop -id "$NEW_WINDOW" _NET_WM_PID 2>/dev/null | awk '{print $NF}')"
  expect_eq "takeover.window-owner" "$NEW_PID" "$OWNER"
  # X11 hands a freed client id straight back to the next process, so the id may
  # legitimately repeat; what must be gone is any window still owned by the old pid.
  REMAINING=0
  for id in $(xwininfo -root -tree 2>/dev/null | grep -F 'Little Tools' | grep -o '0x[0-9a-f]*'); do
    [[ "$(xprop -id "$id" _NET_WM_PID 2>/dev/null | awk '{print $NF}')" == "$OLD_PID" ]] && REMAINING=$((REMAINING + 1))
  done
  expect_eq "takeover.old-windows-gone" "0" "$REMAINING"
  pass "takeover.window-id" "$OLD_WINDOW -> $NEW_WINDOW (X11 may reuse the id)"
else
  fail "takeover.window-owner" "no assistant window for pid $NEW_PID"
fi

# The takeover record is what a later --diagnose reports.
if [[ -n "$(takeover_field FromPid)" ]]; then
  expect_eq "takeover.record.fromVersion" "old-build-111" "$(takeover_field FromVersion)"
  expect_eq "takeover.record.fromPid" "$OLD_PID" "$(takeover_field FromPid)"
else
  fail "takeover.record" "no takeover record written"
fi

# ---------------------------------------- 2. --diagnose explains the live instance
env LITTLETOOLS_BUILD_VERSION=new-build-222 "$DOTNET_ROOT/dotnet" "$APP" --diagnose "$OUT/diagnose-running.json" >/dev/null 2>&1
python3 - "$OUT/diagnose-running.json" "$NEW_PID" "$OLD_PID" <<'PY'
import json, sys
diag, new_pid, old_pid = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
d = json.load(open(diag))
checks = [
    ("diagnose.mode", d.get("mode") == "headless", d.get("mode")),
    ("diagnose.pid", d.get("runningInstancePid") == new_pid, d.get("runningInstancePid")),
    ("diagnose.peerVersion", d.get("peerVersion") == "new-build-222", d.get("peerVersion")),
    ("diagnose.peerSameBuild", d.get("peerSameBuild") is True, d.get("peerSameBuild")),
    ("diagnose.tookOverFrom", (d.get("tookOverFrom") or "").startswith("old-build-111 (pid "), d.get("tookOverFrom")),
    ("diagnose.tookOverFromPid", (d.get("tookOverFrom") or "").endswith("(pid %d)" % old_pid), d.get("tookOverFrom")),
]
code = 0
for name, ok, actual in checks:
    print(("PASS " if ok else "FAIL ") + name + ": " + str(actual))
    if not ok: code = 1
sys.exit(code)
PY
[[ $? -eq 0 ]] || failures=$((failures + 1))

# --------------------------------------------- 3. the same build is just forwarded
BEFORE_PID="$(identity_field Pid)"
env LITTLETOOLS_BUILD_VERSION=new-build-222 "$DOTNET_ROOT/dotnet" "$APP" --todo >/dev/null 2>&1
FORWARD_EXIT=$?
sleep 2
expect_eq "same-build.command-exit" "0" "$FORWARD_EXIT"
expect_eq "same-build.no-takeover" "$BEFORE_PID" "$(identity_field Pid)"
expect_eq "same-build.still-alive" "yes" "$(alive "$NEW_PID" && echo yes || echo no)"
TODO_WINDOW=""
for _ in $(seq 1 20); do TODO_WINDOW="$(window_for_pid "$NEW_PID" 'Daily Todo' || true)"; [[ -n "$TODO_WINDOW" ]] && break; sleep 0.25; done
if [[ -n "$TODO_WINDOW" ]]; then
  pass "same-build.command-delivered" "the running instance opened the todo window ($TODO_WINDOW)"
else
  fail "same-build.command-delivered" "no Daily Todo window for pid $NEW_PID"
fi

# ------------------------------------ 4. an unresponsive old instance is announced
env LITTLETOOLS_BUILD_VERSION=new-build-222 "$DOTNET_ROOT/dotnet" "$APP" --exit >/dev/null 2>&1
sleep 3
rm -f "$IDENTITY" "$TAKEOVER"
python3 - "$PIPE" > "$OUT/fake-peer.log" 2>&1 <<'PY' &
import os, socket, sys
path = sys.argv[1]
try: os.unlink(path)
except FileNotFoundError: pass
server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
server.bind(path)
server.listen(8)
print("fake peer listening", flush=True)
while True:
    connection, _ = server.accept()
    connection.recv(4096)
    connection.sendall(b"99999\n")   # answers "ping" like a pipe owner, never exits
    connection.close()
PY
FAKE_PID=$!
sleep 2
START=$(date +%s)
env LITTLETOOLS_BUILD_VERSION=new-build-222 "$DOTNET_ROOT/dotnet" "$APP" --background > "$OUT/stale-peer.out" 2>&1
STALE_EXIT=$?
ELAPSED=$(( $(date +%s) - START ))
kill "$FAKE_PID" 2>/dev/null
expect_eq "stale-peer.exit-code" "3" "$STALE_EXIT"
if [[ "$ELAPSED" -ge 5 && "$ELAPSED" -le 12 ]]; then
  pass "stale-peer.bounded-wait" "${ELAPSED}s (timeout 6s)"
else
  fail "stale-peer.bounded-wait" "${ELAPSED}s"
fi
if grep -q "没有把命令转交给旧实例" "$OUT/stale-peer.out"; then
  pass "stale-peer.warned" "$(head -c 120 "$OUT/stale-peer.out")"
else
  fail "stale-peer.warned" "$(cat "$OUT/stale-peer.out")"
fi
if [[ -f "$IDENTITY" ]]; then
  fail "stale-peer.no-silent-handoff" "the stale run published itself as primary"
else
  pass "stale-peer.no-silent-handoff" "no new primary was published"
fi

# ------------------------ 5. an older build that never published an identity is superseded
# This is the real-world shape: the installed copy predates the identity file, so
# it answers the pipe but cannot state a version. It understands the existing Exit
# command, so it still steps aside without the new build knowing anything about it.
rm -f "$IDENTITY" "$TAKEOVER"
python3 - "$PIPE" > "$OUT/fake-old-build.log" 2>&1 <<'PY' &
import os, socket, sys
path = sys.argv[1]
try: os.unlink(path)
except FileNotFoundError: pass
server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
server.bind(path)
server.listen(8)
print("fake old build listening", flush=True)
while True:
    connection, _ = server.accept()
    command = connection.recv(4096).decode(errors="replace").strip()
    connection.sendall(b"99999\n")
    connection.close()
    if command == "Exit":
        try: os.unlink(path)
        except FileNotFoundError: pass
        break
server.close()
PY
FAKE2=$!
sleep 2
launch new-build-222 --background
if wait_for_identity 20; then
  expect_eq "old-build.took-over" "new-build-222" "$(identity_field Version)"
  TAKEN_PID="$(identity_field Pid)"
  check_from="$(python3 -c "import json;v=json.load(open('$TAKEOVER')).get('FromVersion');print('' if v is None else v)" 2>/dev/null)"
  expect_eq "old-build.record-fromVersion" "" "$check_from"
  expect_eq "old-build.record-fromPid" "99999" "$(takeover_field FromPid)"
  expect_eq "old-build.record-toVersion" "new-build-222" "$(takeover_field ToVersion)"
  pass "old-build.took-over-pid" "$TAKEN_PID"
  env LITTLETOOLS_BUILD_VERSION=new-build-222 "$DOTNET_ROOT/dotnet" "$APP" --exit >/dev/null 2>&1
  sleep 3
else
  fail "old-build.took-over" "the new build never became primary"
fi
kill "$FAKE2" 2>/dev/null
REMAINING_PID="$(identity_field Pid)"
[[ -n "$REMAINING_PID" ]] && kill "$REMAINING_PID" 2>/dev/null
sleep 1

echo "== $failures failures =="
exit "$failures"

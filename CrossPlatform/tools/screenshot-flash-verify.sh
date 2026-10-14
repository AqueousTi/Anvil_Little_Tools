#!/usr/bin/env bash
# Objective flash evidence for every capture path.
#
# The measurement uses GNOME Shell's ScreenshotArea with flash=false, which does
# not flash, and samples a dark 64x64 patch of the screen every ~10ms
# (screenshot-flash-probe.py); a full-screen white lightbox shows up as a jump of
# ~190 in the mean brightness. Measuring the flash with a flashing tool would
# measure the tool, which is exactly the mistake this harness avoids.
#
# It asserts:
#   shell flash=false (our flash-free backend)   -> 0 white frames
#   shell flash=true  (gnome-screenshot/portal)  -> white frames
#   portal interactive=false                     -> white frames
#   FlashArea (direct positive control)          -> white frames
#
# It also reads the D-Bus traffic, which is independent of the pixels:
#   the app driven through the real entry point must send flash=false;
#   gnome-screenshot must send flash=true.
#
#   screenshot-flash-verify.sh [outdir]
#
# Needs: an X display (default DISPLAY=:1), the workspace Release build,
# .tools/x11tool, python3 + PyGObject + Pillow, dbus-monitor.
set -uo pipefail

WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"

OUT="${1:-$WORKSPACE_ROOT/.tools/out/screenshot-flash}"
export DISPLAY="${DISPLAY:-:1}"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
mkdir -p "$OUT" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME"

PROBE="$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-flash-probe.py"
PATTERN_TOOL="$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-pattern-window.py"

pass=0; fail=0
ok()  { echo "PASS  $1"; pass=$((pass + 1)); }
bad() { echo "FAIL  $1"; fail=$((fail + 1)); }

# A dark, stable patch: the flash is only visible as a jump when the baseline is
# near black.
WX=1600; WY=1000; WW=360; WH=260
X1=$((WX + 100)); Y1=$((WY + 100)); S=64
PATTERN="$OUT/dark.png"
python3 "$PATTERN_TOOL" generate "$WW" "$WH" "$PATTERN" dark

PATTERN_PID=""
cleanup() { [[ -n "$PATTERN_PID" ]] && kill "$PATTERN_PID" 2>/dev/null; }
trap cleanup EXIT

python3 "$PATTERN_TOOL" show "$WX" "$WY" "$WW" "$WH" "$PATTERN" >"$OUT/pattern-window.log" 2>&1 &
PATTERN_PID=$!
sleep 3
if ! xwininfo -root -children 2>/dev/null | grep -q "${WW}x${WH}+${WX}+${WY}"; then
  echo "the dark pattern window did not appear; log:" >&2
  cat "$OUT/pattern-window.log" >&2
  exit 2
fi

echo "--- frame sampling (patch ${S}x${S}+${X1}+${Y1}) ---"
if timeout 120 python3 "$PROBE" "$X1" "$Y1" "$S" "$S" "$OUT/flash.txt"; then
  cat "$OUT/flash.txt" | sed 's/^/    /'
  verdict() {
    local trial="$1" want="$2"
    local line
    line="$(grep "^$trial " "$OUT/flash.txt" || true)"
    local white
    white="$(awk '{for (i = 1; i <= NF; i++) if ($i ~ /^white=/) {sub("white=", "", $i); print $i}}' <<<"$line")"
    if [[ -z "$white" ]]; then
      bad "$trial: no measurement was recorded"
    elif [[ "$want" == "white" && "$white" -gt 0 ]]; then
      ok "$trial: flashes ($white white frames)"
    elif [[ "$want" == "white" ]]; then
      bad "$trial: expected white frames, saw none"
    elif [[ "$white" == 0 ]]; then
      ok "$trial: no flash (0 white frames)"
    else
      bad "$trial: expected no flash, saw $white white frames"
    fi
  }
  verdict "shell-off" none
  verdict "shell-on" white
  verdict "portal-1" white
  verdict "flash-area" white
  idle_line="$(grep '^idle ' "$OUT/flash.txt" || true)"
  if [[ -n "$idle_line" && "$(awk '{for (i=1;i<=NF;i++) if ($i ~ /^white=/) {sub("white=","",$i); print $i}}' <<<"$idle_line")" == "0" ]]; then
    ok "idle: the baseline has no white frames"
  else
    bad "idle: the baseline is not clean ($idle_line)"
  fi
else
  bad "the flash probe could not run"
fi

# --- D-Bus evidence: what the real entry point and gnome-screenshot send --------
read_screenshot_calls() {
  # Prints one "cursor,flash" line per org.gnome.Shell.Screenshot.Screenshot call.
  python3 - "$1" <<'PY'
import re
import sys

text = open(sys.argv[1], encoding="utf-8", errors="replace").read()
lines = text.splitlines()
for index, line in enumerate(lines):
    if "member=Screenshot" not in line or "interface=org.gnome.Shell.Screenshot;" not in line:
        continue
    values = []
    for follow in lines[index + 1:index + 6]:
        match = re.match(r"\s+boolean (true|false)$", follow)
        if match:
            values.append(match.group(1))
        elif values:
            break
    if len(values) >= 2:
        print(f"{values[0]},{values[1]}")
PY
}

echo "--- the app's own call (forced shell backend) ---"
timeout 60 dbus-monitor --session "interface='org.gnome.Shell.Screenshot',member='Screenshot'" \
  >"$OUT/app-monitor.log" 2>&1 &
MONITOR_PID=$!
sleep 1.5
LITTLETOOLS_FORCE_SCREENSHOT_BACKEND=shell \
  "$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-region-verify.sh" "$OUT/app" cli "$WX" "$WY" "$((WX + WW))" "$((WY + WH))" \
  >"$OUT/app-region.log" 2>&1
verify=$?
sleep 0.5
kill "$MONITOR_PID" 2>/dev/null
wait "$MONITOR_PID" 2>/dev/null
[[ "$verify" == 0 ]] && ok "the entry point completed while the bus was monitored" \
                     || bad "the entry point failed while the bus was monitored (exit=$verify)"
read_screenshot_calls "$OUT/app-monitor.log" > "$OUT/app-calls.txt"
echo "    calls (include_cursor,flash): $(tr '\n' ' ' < "$OUT/app-calls.txt")"
if grep -q "^false,false$" "$OUT/app-calls.txt"; then
  ok "the app's own capture calls the shell with flash=false"
else
  bad "the app's own capture did not send flash=false"
fi

echo "--- gnome-screenshot's own call ---"
timeout 40 dbus-monitor --session "interface='org.gnome.Shell.Screenshot',member='Screenshot'" \
  >"$OUT/gnome-screenshot-monitor.log" 2>&1 &
MONITOR_PID=$!
sleep 1.5
gnome-screenshot -f "$OUT/gnome-screenshot.png" >"$OUT/gnome-screenshot.log" 2>&1
sleep 0.5
kill "$MONITOR_PID" 2>/dev/null
wait "$MONITOR_PID" 2>/dev/null
read_screenshot_calls "$OUT/gnome-screenshot-monitor.log" > "$OUT/gnome-screenshot-calls.txt"
echo "    calls (include_cursor,flash): $(tr '\n' ' ' < "$OUT/gnome-screenshot-calls.txt")"
if grep -q "^false,true$" "$OUT/gnome-screenshot-calls.txt"; then
  ok "gnome-screenshot (the last-resort tool) sends flash=true"
else
  bad "gnome-screenshot did not send flash=true; the comparison is moot"
fi

echo
echo "evidence: $OUT/flash.txt  $OUT/app-monitor.log  $OUT/gnome-screenshot-monitor.log"
echo "pass=$pass fail=$fail"
[[ "$fail" == 0 ]]

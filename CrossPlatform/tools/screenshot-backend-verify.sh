#!/usr/bin/env bash
# Real-desktop verification of the screenshot backends.
#
# It proves, on the running GNOME/X11 session:
#   1. the flash-free shell backend is used when it is forced, and the cropped
#      result is the selected region of the real screen, pixel for pixel;
#   2. the portal backend is used when it is forced, produces the same picture,
#      and removes the file it wrote into the user's Pictures directory;
#   3. gnome-screenshot still works immediately after our capture, i.e. the
#      org.gnome.Screenshot name we lease was really released (checked through
#      gnome-screenshot's own stderr: the "resorting to fallback X11" warning must
#      not appear);
#   4. with the session bus broken the whole chain falls back to the tool chain,
#      silently, and still crops the right region.
#
# The screen content is a picture this harness plants itself
# (screenshot-pattern-window.py) and whose pixels it knows, because a live desktop
# changes under the test. The flash-free gnome-shell ScreenshotArea probe is used
# as the independent reference: it also proves the planted window really shows
# those pixels before any comparison is made.
#
#   screenshot-backend-verify.sh [outdir]
#
# Needs: an X display (default DISPLAY=:1), the workspace Release build,
# .tools/x11tool, python3 + PyGObject + Pillow, and a GNOME session with
# org.gnome.Shell.Screenshot reachable.
set -uo pipefail

WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"

OUT="${1:-$WORKSPACE_ROOT/.tools/out/screenshot-backend}"
export DISPLAY="${DISPLAY:-:1}"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
mkdir -p "$OUT" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME"

PROBE="$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-area-probe.py"
PATTERN_TOOL="$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-pattern-window.py"
COMPARE="$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-crop-compare.py"
LAST_BACKEND="$XDG_CACHE_HOME/little-tools/last-screenshot.json"
PICTURES="$(xdg-user-dir PICTURES 2>/dev/null || echo "$HOME/Pictures")"

pass=0; fail=0
ok()  { echo "PASS  $1"; pass=$((pass + 1)); }
bad() { echo "FAIL  $1"; fail=$((fail + 1)); }

# The planted picture and the drag that selects exactly it.
PX=300; PY=300; PW=900; PH=560
X2=$((PX + PW)); Y2=$((PY + PH))
PATTERN="$OUT/pattern.png"
python3 "$PATTERN_TOOL" generate "$PW" "$PH" "$PATTERN"

PATTERN_PID=""
cleanup() {
  [[ -n "$PATTERN_PID" ]] && kill "$PATTERN_PID" 2>/dev/null
  [[ -n "${SHIM_DIR:-}" ]] && rm -rf "$SHIM_DIR"
}
trap cleanup EXIT

python3 "$PATTERN_TOOL" show "$PX" "$PY" "$PW" "$PH" "$PATTERN" >"$OUT/pattern-window.log" 2>&1 &
PATTERN_PID=$!
sleep 3
if ! xwininfo -root -children 2>/dev/null | grep -q "${PW}x${PH}+${PX}+${PY}"; then
  echo "the pattern window did not appear at ${PW}x${PH}+${PX}+${PY}; log:" >&2
  cat "$OUT/pattern-window.log" >&2
  exit 2
fi

# Independent reference through the flash-free ScreenshotArea call.
if timeout 30 python3 "$PROBE" area "$PX" "$PY" "$PW" "$PH" "$OUT/reference.png"; then
  if python3 "$COMPARE" "$PATTERN" "$OUT/reference.png" 0 0 "$PW" "$PH" > "$OUT/reference-compare.txt" 2>&1; then
    ok "the planted picture is on screen unchanged (flash-free ScreenshotArea reference)"
  else
    bad "ScreenshotArea does not read the planted picture back"
    sed 's/^/    /' "$OUT/reference-compare.txt"
  fi
else
  bad "the flash-free ScreenshotArea reference could not be taken"
fi

# Runs the real screenshot-translation entry point with one pinned backend and
# checks the recorded backend plus the cropped pixels.
run_backend() {
  local backend="$1" label="$2"
  local dir="$OUT/$label" crop="$OUT/$label-crop.png"
  rm -rf "$dir"; mkdir -p "$dir"; rm -f "$crop"
  local before after
  before="$(ls "$PICTURES" 2>/dev/null | sort)"
  LITTLETOOLS_FORCE_SCREENSHOT_BACKEND="$backend" \
  LITTLETOOLS_SCREENSHOT_CROP_OUT="$crop" \
    "$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-region-verify.sh" "$dir" cli "$PX" "$PY" "$X2" "$Y2" \
    >"$OUT/$label-region.log" 2>&1
  local verify=$?
  [[ "$verify" == 0 ]] && ok "$label: the screenshot-translation entry point completed" \
                      || bad "$label: screenshot-region-verify failed (exit=$verify)"
  tail -8 "$OUT/$label-region.log" | sed 's/^/    /'

  local recorded
  recorded="$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['backend'])" "$LAST_BACKEND" 2>/dev/null)"
  [[ "$recorded" == "$backend" ]] && ok "$label: --diagnose reports the backend that ran ($recorded)" \
                                  || bad "$label: recorded backend '$recorded' != '$backend'"

  if [[ -f "$crop" ]] && python3 "$COMPARE" "$PATTERN" "$crop" 0 0 "$PW" "$PH" >"$OUT/$label-compare.txt" 2>&1; then
    ok "$label: the crop is the selected region of the real screen, pixel for pixel"
    sed 's/^/    /' "$OUT/$label-compare.txt"
  else
    bad "$label: the crop does not match the selected region"
    [[ -f "$crop" ]] && sed 's/^/    /' "$OUT/$label-compare.txt" || echo "    (no crop was written)" >&2
  fi

  after="$(ls "$PICTURES" 2>/dev/null | sort)"
  if [[ "$before" == "$after" ]]; then
    ok "$label: the user's Pictures directory is unchanged"
  else
    bad "$label: the capture left files in $PICTURES"
    diff <(printf '%s\n' "$before") <(printf '%s\n' "$after") | sed 's/^/    /'
  fi
}

run_backend shell shell
run_backend portal portal

# The name lease must be released: gnome-screenshot has to still reach the shell
# interface instead of falling back to its X11 path.
GNOME_SHOT="$OUT/gnome-screenshot-after.png"
SHOT_LOG="$OUT/gnome-screenshot-after.log"
if gnome-screenshot -f "$GNOME_SHOT" >"$SHOT_LOG" 2>&1 && [[ -s "$GNOME_SHOT" ]]; then
  if grep -qi "fallback" "$SHOT_LOG"; then
    bad "gnome-screenshot fell back to X11 after our capture: the leased name was not released"
    sed 's/^/    /' "$SHOT_LOG"
  else
    ok "gnome-screenshot still uses the shell interface right after our capture (name released)"
  fi
else
  bad "gnome-screenshot failed right after our capture"
  sed 's/^/    /' "$SHOT_LOG"
fi

# --- fallback: no session bus, the whole chain must reach the tools ------------
SHIM_DIR="/tmp/lt-backend-shims"
rm -rf "$SHIM_DIR"; mkdir -p "$SHIM_DIR"
PATTERN_SHIM_LOG="$OUT/fallback-shim-calls.log"
: > "$PATTERN_SHIM_LOG"
cat > "$SHIM_DIR/pattern.py" <<'PY'
import sys
from PIL import Image
out = sys.argv[1]
w, h = int(sys.argv[2]), int(sys.argv[3])
red = bytes((((x & 0xFF) + (x >> 8) * 53) & 0xFF) for x in range(w))
blue = {off: bytes((((x * 11) + off) & 0xFF) for x in range(w)) for off in range(0, 256, 37)}
rows = []
for y in range(h):
    row = bytearray(w * 3)
    row[0::3] = red
    row[1::3] = bytes([y & 0xFF]) * w
    row[2::3] = blue[(y >> 8) * 37]
    rows.append(bytes(row))
Image.frombytes('RGB', (w, h), b''.join(rows)).save(out)
PY
read -r SCREEN_W SCREEN_H <<<"$(xdpyinfo | awk '/dimensions:/{split($2,a,"x"); print a[1], a[2]}')"
python3 "$SHIM_DIR/pattern.py" "$OUT/fallback-pattern.png" "$SCREEN_W" "$SCREEN_H"
for tool in maim scrot import grim spectacle; do
  printf '%s\n' "#!/usr/bin/env bash
echo \"$tool \$*\" >> \"$PATTERN_SHIM_LOG\"
exec python3 \"$SHIM_DIR/pattern.py\" \"\${@: -1}\" \"$SCREEN_W\" \"$SCREEN_H\"" > "$SHIM_DIR/$tool"
  chmod +x "$SHIM_DIR/$tool"
done
# gnome-screenshot/xwd are deliberately absent as shims so the first silent tool is
# the one the chain must pick, and reaching xwd would be visible as a failure.

FALLBACK_CROP="$OUT/fallback-crop.png"
rm -f "$FALLBACK_CROP"
DBUS_SESSION_BUS_ADDRESS="unix:path=/tmp/lt-no-such-session-bus" \
PATH="$SHIM_DIR:$PATH" \
LITTLETOOLS_SCREENSHOT_CROP_OUT="$FALLBACK_CROP" \
  "$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-region-verify.sh" "$OUT/fallback" cli "$PX" "$PY" "$X2" "$Y2" \
  >"$OUT/fallback-region.log" 2>&1
verify=$?
echo "--- fallback (broken session bus) ---"
tail -10 "$OUT/fallback-region.log" | sed 's/^/    /'
[[ "$verify" == 0 ]] && ok "fallback: the entry point still completed with no session bus" \
                     || bad "fallback: the entry point failed with no session bus (exit=$verify)"
recorded="$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['backend'])" "$LAST_BACKEND" 2>/dev/null)"
grep -q "little-tools-screen" "$PATTERN_SHIM_LOG" \
  && ok "fallback: a capture tool was used (recorded backend: ${recorded:-none})" \
  || bad "fallback: no capture tool was called"
grep -q "^xwd " "$PATTERN_SHIM_LOG" && bad "fallback: the raw xwd dump was reached" \
                                     || ok "fallback: the raw xwd dump was never reached"
if [[ -f "$FALLBACK_CROP" ]] && python3 "$COMPARE" "$OUT/fallback-pattern.png" "$FALLBACK_CROP" "$PX" "$PY" "$X2" "$Y2" >"$OUT/fallback-compare.txt" 2>&1; then
  ok "fallback: the crop is the selected region of the pattern, pixel for pixel"
  sed 's/^/    /' "$OUT/fallback-compare.txt"
else
  bad "fallback: the crop does not match the selected region"
  [[ -f "$FALLBACK_CROP" ]] && sed 's/^/    /' "$OUT/fallback-compare.txt"
fi

echo
echo "evidence: $OUT/{reference,shell-compare,portal-compare,fallback-compare}.txt"
echo "pass=$pass fail=$fail"
[[ "$fail" == 0 ]]

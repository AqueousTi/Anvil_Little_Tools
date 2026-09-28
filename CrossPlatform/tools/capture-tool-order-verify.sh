#!/usr/bin/env bash
# Verifies the full-desktop capture chain prefers the tools that do not flash.
#
# Why this exists: gnome-screenshot always fires a screen-wide white shutter when
# it captures. On a GNOME session its shell backend asks
# org.gnome.Shell.Screenshot for flash=true (GNOME Shell answers with a Flashspot
# lightbox); the X11 fallback fires gnome-screenshot's own bundled CheeseFlash.
# Version 41.0 has no flag, environment variable or gsettings key for it, so the
# only fix is to try the silent tools first (maim, scrot, import, grim, spectacle)
# and leave gnome-screenshot as the last resort. This harness proves the app does
# that on the real X display.
#
# It works by putting shims on PATH. The shims never capture for real (they draw a
# deterministic pattern with Pillow instead), so the run itself never flashes, and
# each shim appends its own invocation to $LT_SHIM_LOG. The app is then driven
# through the real "screenshot translation" entry point by
# screenshot-region-verify.sh, and the log tells which tool the app really used.
#
#   capture-tool-order-verify.sh [outdir]
#
# Two modes are checked:
#   present  - every tool has a shim: the app must call maim and nothing else;
#   fallback - only gnome-screenshot and xwd have shims: the app must call
#              gnome-screenshot and must NOT reach the raw xwd dump.
#
# The shims write whole-desktop patterns, so the harness's own overlay/shotwin
# screenshots would be patterns too. To keep that evidence real, the
# gnome-screenshot shim only fakes the app's own temp captures
# (little-tools-screen-*.png under the isolated TMPDIR) and delegates every other
# call to /usr/bin/gnome-screenshot.
#
# Needs: an X display (default DISPLAY=:1), python3 + Pillow, the workspace
# Release build, and .tools/x11tool.
set -uo pipefail

WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"

OUT="${1:-$WORKSPACE_ROOT/.tools/out/capture-tool-order}"
export DISPLAY="${DISPLAY:-:1}"
export XDG_CONFIG_HOME="$WORKSPACE_ROOT/.tools/xdg/config"
export XDG_DATA_HOME="$WORKSPACE_ROOT/.tools/xdg/data"
export XDG_CACHE_HOME="$WORKSPACE_ROOT/.tools/xdg/cache"
mkdir -p "$OUT" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME" "$XDG_CACHE_HOME"

# The pattern must be the size of the X root window, which is what the app's
# capture tools dump and what its selection coordinates are indexed from.
read -r SCREEN_W SCREEN_H <<<"$(xdpyinfo | awk '/dimensions:/{split($2,a,"x"); print a[1], a[2]}')"
if [[ -z "${SCREEN_W:-}" || -z "${SCREEN_H:-}" ]]; then
  echo "could not read the X root size from xdpyinfo" >&2
  exit 2
fi

pass=0; fail=0
ok()  { echo "PASS  $1"; pass=$((pass + 1)); }
bad() { echo "FAIL  $1"; fail=$((fail + 1)); }

pattern_py() {
  cat > "$1" <<'PY'
import sys
from PIL import Image
out = sys.argv[1]
w, h = int(sys.argv[2]), int(sys.argv[3])
# A per-pixel ramp that differs for any offset smaller than the screen: the red
# channel also steps every 256 columns, the blue channel every 256 rows, so even a
# whole-tile shift is visible when the crop is compared against the full pattern.
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
}

make_shim() {
  local dir="$1" name="$2" script="$3"
  printf '%s\n' "$script" > "$dir/$name"
  chmod +x "$dir/$name"
}

run_mode() {
  local mode="$1"; shift
  local tools=("$@")
  local dir="/tmp/lt-capture-shims-$mode"
  local log="$OUT/$mode-shim-calls.log"
  rm -rf "$dir"; mkdir -p "$dir"
  : > "$log"
  pattern_py "$dir/pattern.py"

  local tool
  for tool in "${tools[@]}"; do
    if [[ "$tool" == "gnome-screenshot" ]]; then
      # Only the app's own temp capture is faked; the harness's overlay/shotwin
      # screenshots delegate to the real tool so that evidence stays genuine.
      make_shim "$dir" "$tool" "#!/usr/bin/env bash
echo \"$tool \$*\" >> \"$log\"
for arg in \"\$@\"; do
  if [[ \"\$arg\" == *little-tools-screen-* ]]; then
    exec python3 \"$dir/pattern.py\" \"\$arg\" \"$SCREEN_W\" \"$SCREEN_H\"
  fi
done
exec /usr/bin/gnome-screenshot \"\$@\""
    elif [[ "$tool" == "xwd" ]]; then
      # Reaching xwd means every decoder above it failed; report it loudly instead
      # of silently succeeding with a dump the app would reject when black.
      make_shim "$dir" "$tool" "#!/usr/bin/env bash
echo \"$tool \$*\" >> \"$log\"
exit 1"
    else
      make_shim "$dir" "$tool" "#!/usr/bin/env bash
echo \"$tool \$*\" >> \"$log\"
exec python3 \"$dir/pattern.py\" \"\${@: -1}\" \"$SCREEN_W\" \"$SCREEN_H\""
    fi
  done

  echo "--- mode=$mode shims: $(ls "$dir" | grep -v pattern.py | tr '\n' ' ')"
  LT_SHIM_LOG="$log" PATH="$dir:$PATH" \
    "$WORKSPACE_ROOT/CrossPlatform/tools/screenshot-region-verify.sh" "$OUT/$mode" cli 600 400 1400 800
  local verify=$?
  [[ "$verify" == 0 ]] && ok "$mode: screenshot-region-verify passed (overlay, drag, window came back)" \
                      || bad "$mode: screenshot-region-verify failed (exit=$verify)"

  echo "  app capture calls:"
  grep "little-tools-screen" "$log" | sed 's/^/    /' || echo "    (none)"
  local used
  used="$(grep "little-tools-screen" "$log" | awk '{print $1}' | sort -u | tr '\n' ' ')"
  echo "  tools used for the app capture: ${used:-none}"
  printf '%s' "$used" > "$OUT/$mode-used.txt"
}

echo "root screen ${SCREEN_W}x${SCREEN_H}; outdir $OUT"

run_mode present maim scrot import grim spectacle gnome-screenshot xwd
used="$(cat "$OUT/present-used.txt")"
[[ "$used" == "maim " ]] && ok "present: maim is called first and no other tool follows" \
                         || bad "present: expected only maim, saw '${used:-none}'"

run_mode fallback gnome-screenshot xwd
used="$(cat "$OUT/fallback-used.txt")"
[[ "$used" == "gnome-screenshot " ]] && ok "fallback: gnome-screenshot is used when no silent tool exists" \
                                     || bad "fallback: expected only gnome-screenshot, saw '${used:-none}'"
grep -q "^xwd " "$OUT/fallback-shim-calls.log" && bad "fallback: the raw xwd dump was reached" \
                                               || ok "fallback: the raw xwd dump was never reached"

echo
echo "tools used, per mode, are in $OUT/*-shim-calls.log"
echo "pass=$pass fail=$fail"
[[ "$fail" == 0 ]]

#!/usr/bin/env bash
# Local test helper (dev-only): drive the *real* detail window through the
# reported flow and prove the chart always ends up describing the selection:
#
#   510300 -> 513500 (tab) -> 600519 (search box) -> 510300 (tab) -> rapid clicks
#
# Two things make this reliable: the pointer is parked inside the window between
# steps (the detail window closes itself when it loses focus with the pointer
# outside), and every step waits for the refresh to settle. While the refresh runs
# the window still shows the previous selection's chart and the status is
# "正在查询…", so a screenshot taken too early proves nothing - the y-axis label
# column is hashed instead, which only changes when the drawn price range changes.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export DISPLAY="${DISPLAY:-:1}"
out="$ROOT/.tools/out"
pid="$(cat "$ROOT/.tools/stkverify/app.pid")"

find_win() {
  local needle="$1" id p
  for id in $(xwininfo -root -tree | grep -F "$needle" | awk '{print $1}'); do
    p=$(xprop -id "$id" _NET_WM_PID 2>/dev/null | grep -oE '[0-9]+$')
    [[ "$p" == "$pid" ]] && { echo "$id"; return; }
  done
}
details() { find_win '股票观察明细'; }
capsule() { find_win '股票观察"'; }
geom() { xwininfo -id "$1" | awk '/Absolute upper-left X/{x=$4} /Absolute upper-left Y/{y=$4} /Width:/{w=$2} /Height:/{h=$2} END{print x, y, w, h}'; }
move() { "$ROOT/.tools/x11tool" move "$1" "$2" >/dev/null; }
click() { move "$1" "$2"; sleep 0.3; "$ROOT/.tools/x11tool" click "$1" "$2" >/dev/null; }
key() { "$ROOT/.tools/x11tool" send "$1" none >/dev/null; }
park() { read -r x y w h < <(geom "$(details)"); move $(( x + 40 )) $(( y + h - 30 )); }

signature() {
  local d x y
  d="$(details)"
  read -r x y _ _ < <(geom "$d")
  gnome-screenshot -f "$out/.race-probe.png" >/dev/null 2>&1
  python3 "$ROOT/CrossPlatform/tools/race-signature.py" "$out/.race-probe.png" "$x" "$y"
}

settle() {
  # the refresh costs two eastmoney attempts (400+1200ms) plus a fallback fetch,
  # and the window keeps the previous chart until the new series lands.
  sleep 12
  local prev="" same=0 sig
  for _ in $(seq 1 24); do
    [[ -z "$(details)" ]] && { echo "  detail window closed while waiting"; return 1; }
    park; sleep 0.5
    sig="$(signature)"
    if [[ "$sig" == "$prev" ]]; then same=$((same+1)); else same=0; fi
    prev="$sig"
    [[ $same -ge 3 ]] && { echo "  labels-signature=$sig"; return 0; }
  done
  echo "  chart never settled (last signature $prev)"
  return 1
}

shot() {
  [[ -z "$(details)" ]] && { echo "$1: detail window is gone"; return 1; }
  settle || { echo "$1: chart did not settle"; return 1; }
  park; sleep 0.4
  "$ROOT/CrossPlatform/tools/shotwin.sh" "$pid" 明细 "$out/$1.png" >/dev/null
  python3 "$ROOT/CrossPlatform/tools/chart-pixels.py" "$out/$1.png" 40 285 385 215 "$1"
}

search_code() {
  local code="$1"
  click 250 368
  key End
  for _ in $(seq 1 8); do key BackSpace; done
  for (( i=0; i<${#code}; i++ )); do key "${code:$i:1}"; done
  key Return
}

# Clicking the capsule toggles the detail window, so only open it when it is closed.
if [[ -z "$(details)" ]]; then
  read -r x y _ _ < <(geom "$(capsule)")
  click $(( x + 158 )) $(( y + 46 ))
  sleep 3
fi

# The y-label column only changes when the drawn price range changes, so it
# identifies the instrument on screen. Selecting the code through the search box is
# used for the labelled steps because the box shows the code (the tab strip scrolls
# and its pixel positions therefore do not map to fixed codes).
LAST_SIG=""
label_of() {
  LAST_SIG="$(signature)"
  echo "  labels-signature=$LAST_SIG"
}

echo "--- step 1: 510300 (daily 1y, live) ---"
search_code 510300; shot race1-510300; label_of
H510300="$LAST_SIG"

echo "--- step 2: 513500 ---"
search_code 513500; shot race2-513500; label_of
H513500="$LAST_SIG"

echo "--- step 3: 600519 ---"
search_code 600519; shot race3-600519; label_of
H600519="$LAST_SIG"

echo "--- step 4: back to 510300 ---"
search_code 510300; shot race4-510300; label_of
H4300="$LAST_SIG"

echo "--- step 5: rapid clicking two tab positions x6 without waiting ---"
for _ in 1 2 3; do click 394 411; click 191 411; done
shot race5-rapid; label_of

echo "--- step 6: 510300 again after the storm ---"
search_code 510300; shot race6-after-rapid-510300; label_of
H6300="$LAST_SIG"

echo
echo "signatures: 510300=$H510300/$H4300/$H6300  513500=$H513500  600519=$H600519"
if [[ "$H510300" == "$H4300" && "$H510300" == "$H6300" && "$H513500" != "$H510300" && "$H600519" != "$H510300" && "$H600519" != "$H513500" ]]; then
  echo "INVARIANT OK: every screenshot shows the series of the code it was taken for"
else
  echo "INVARIANT FAILED"
fi

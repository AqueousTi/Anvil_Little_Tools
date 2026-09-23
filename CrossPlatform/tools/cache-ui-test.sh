#!/usr/bin/env bash
# Local test helper (dev-only): prove the cached-chart behaviour on the real UI.
#
#   1. 510300 first visit   -> chart appears (network, this is the slow path)
#   2. 513500 first visit   -> chart appears (network, no cache yet)
#   3. back to 510300       -> chart appears from the cache, measured in ms
#   4. re-query 510300      -> the chart is never blanked while refreshing
#   5. close + reopen       -> the chart is there again from the cache
#
# The time to chart is measured as the first poll whose y-axis label column matches
# the signature captured for that code, plus the screenshot of that moment. Screen
# grabbing granularity is ~0.2-0.3s, which is stated with the numbers.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export DISPLAY="${DISPLAY:-:1}"
out="$ROOT/.tools/out"
pid="$(cat "$ROOT/.tools/stkverify/app.pid")"

find_win() { local needle="$1" id p; for id in $(xwininfo -root -tree | grep -F "$needle" | awk '{print $1}'); do p=$(xprop -id "$id" _NET_WM_PID 2>/dev/null | grep -oE '[0-9]+$'); [[ "$p" == "$pid" ]] && { echo "$id"; return; }; done; }
details() { find_win '股票观察明细'; }
capsule() { find_win '股票观察"'; }
geom() { xwininfo -id "$1" | awk '/Absolute upper-left X/{x=$4} /Absolute upper-left Y/{y=$4} /Width:/{w=$2} /Height:/{h=$2} END{print x, y, w, h}'; }
move() { "$ROOT/.tools/x11tool" move "$1" "$2" >/dev/null; }
click() { move "$1" "$2"; sleep 0.2; "$ROOT/.tools/x11tool" click "$1" "$2" >/dev/null; }
key() { "$ROOT/.tools/x11tool" send "$1" none >/dev/null; }
park() { read -r x y w h < <(geom "$(details)"); move $(( x + 40 )) $(( y + h - 30 )); }
signature() { local d x y; d="$(details)"; read -r x y _ _ < <(geom "$d"); gnome-screenshot -f "$out/.cache-probe.png" >/dev/null 2>&1; python3 "$ROOT/CrossPlatform/tools/race-signature.py" "$out/.cache-probe.png" "$x" "$y"; }
snap() { local d; d="$(details)"; park; sleep 0.2; "$ROOT/CrossPlatform/tools/shotwin.sh" "$pid" 明细 "$out/$1.png" >/dev/null; }

search_code() { local code="$1"; click 250 368; key End; for _ in $(seq 1 8); do key BackSpace; done; for (( i=0; i<${#code}; i++ )); do key "${code:$i:1}"; done; key Return; }

# wait_for <expected signature> <timeout seconds> -> prints elapsed milliseconds
wait_for() {
  local want="$1" timeout="$2" start ms sig
  start="$(date +%s%3N)"
  while :; do
    sig="$(signature)"
    ms=$(( $(date +%s%3N) - start ))
    if [[ "$sig" == "$want" ]]; then echo "$ms"; return 0; fi
    (( ms > timeout * 1000 )) && { echo "$ms(timeout,sig=$sig)"; return 1; }
  done
}

if [[ -z "$(details)" ]]; then
  read -r x y _ _ < <(geom "$(capsule)")
  click $(( x + 158 )) $(( y + 46 ))
  sleep 2
fi

echo "--- warm up 510300 (first visit, network) ---"
search_code 510300
sleep 12
S510300="$(signature)"; echo "  S510300=$S510300"
snap cache-1-510300

echo "--- first visit to 513500 (network, no cache) ---"
search_code 513500
sleep 12
S513500="$(signature)"; echo "  S513500=$S513500"
snap cache-2-513500

echo "--- back to 510300 (cached) ---"
search_code 510300
ms="$(wait_for "$S510300" 15)" && echo "  time from the click to 510300's chart: ${ms}ms" || echo "  FAILED: $ms"
snap cache-3-back-510300

echo "--- re-query 510300: the chart must not be blanked while refreshing ---"
search_code 510300
sleep 0.8
snap cache-4-during-refresh
echo "--- after the refresh settles ---"
sleep 10
snap cache-5-after-refresh

echo "--- close the detail window and reopen it (cached) ---"
read -r x y _ _ < <(geom "$(capsule)")
click $(( x + 158 )) $(( y + 46 ))          # close
sleep 2
[[ -n "$(details)" ]] && echo "  window still open?" 
click $(( x + 158 )) $(( y + 46 ))          # reopen
ms="$(wait_for "$S510300" 15)" && echo "  time from the reopen click to 510300's chart: ${ms}ms" || echo "  FAILED: $ms"
snap cache-6-reopened

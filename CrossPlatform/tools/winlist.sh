#!/usr/bin/env bash
# Local test helper (dev-only): list every suite top level window on the test
# display with its PID, geometry, map state and _NET_WM_STATE, so a report can
# state exactly which window appeared and whether the WM was told to skip the
# taskbar.
#
#   winlist.sh [pid]      only windows owned by that pid
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export DISPLAY="${DISPLAY:-:1}"
want="${1:-}"
xwininfo -root -tree 2>/dev/null | grep -F '"Little Tools' | while read -r line; do
  id="$(echo "$line" | awk '{print $1}')"
  title="$(echo "$line" | sed -n 's/.*"\(Little Tools[^"]*\)".*/\1/p')"
  geom="$(echo "$line" | grep -oE '[0-9]+x[0-9]+\+[-0-9]+\+[-0-9]+' | head -1)"
  pid="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | grep -oE '[0-9]+$')"
  [[ -n "$want" && "$pid" != "$want" ]] && continue
  state="$(xprop -id "$id" _NET_WM_STATE 2>/dev/null | sed 's/^_NET_WM_STATE[^=]*= *//')"
  mapstate="$(xwininfo -id "$id" 2>/dev/null | sed -n 's/.*Map State: *//p')"
  wmclass="$(xprop -id "$id" WM_CLASS 2>/dev/null | sed 's/^WM_CLASS[^=]*= *//')"
  printf '%s\tpid=%s\t%s\tmap=%s\tclass=%s\n\tstate=%s\n' \
    "$id" "$pid" "$geom" "$mapstate" "$wmclass" "$state"
  echo "	title=$title"
done

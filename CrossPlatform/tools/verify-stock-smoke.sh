#!/usr/bin/env bash
# Local test helper (dev-only): run --stock-smoke repeatedly and prove the result
# does not depend on leftover state.
#
#   verify-stock-smoke.sh <polluted|clean> <rounds>
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
mode="$1"; rounds="${2:-3}"
source "$ROOT/CrossPlatform/tools/env.sh"
export XDG_CONFIG_HOME="$ROOT/.tools/xdg/config" XDG_DATA_HOME="$ROOT/.tools/xdg/data" \
  XDG_CACHE_HOME="$ROOT/.tools/xdg/cache" DISPLAY="${DISPLAY:-:1}"
APP="$ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
OUT="$ROOT/.tools/out/verify-$mode"
POLLUTED='{"Watched":[{"Code":"600519","AlertEnabled":true,"PremiumThreshold":2}],"SelectedCode":"600519","Topmost":true,"Compact":false,"KlinePeriod":"Monthly","RangeYears":5,"Left":855,"Top":530}'

rm -rf "$OUT"; mkdir -p "$OUT"
for round in $(seq 1 "$rounds"); do
  if [[ "$mode" == polluted ]]; then
    # Both channels that used to leak state into the run: the smoke's own
    # settings.json from the previous round, and StockStore's legacy import.
    mkdir -p "$OUT/data"
    printf '%s' "$POLLUTED" > "$OUT/data/settings.json"
    printf '%s' "$POLLUTED" > "$ROOT/.tools/out/verify-polluted-source.json"
    export LITTLETOOLS_STOCK_DATA="$ROOT/.tools/out/verify-polluted-source.json"
    # and the real app data file the smoke must never read
    mkdir -p "$ROOT/.tools/xdg/data/little-tools/stock"
    printf '%s' "$POLLUTED" > "$ROOT/.tools/xdg/data/little-tools/stock/settings.json"
  else
    unset LITTLETOOLS_STOCK_DATA
    rm -rf "$OUT/data"
  fi
  "$DOTNET_ROOT/dotnet" "$APP" --stock-smoke "$OUT" >"$OUT/run-$round.log" 2>&1
  code=$?
  pngs=$(ls "$OUT"/*.png 2>/dev/null | wc -l)
  state=$(sed -n 's/^state: \([^|]*\).*/\1/p' "$OUT/stock-chart.txt" 2>/dev/null | head -1)
  error=$(head -1 "$OUT.error.txt" 2>/dev/null)
  printf 'round %s: exit=%s pngs=%s %s\n' "$round" "$code" "$pngs" "$state"
  [[ -n "$error" ]] && printf '         error: %s\n' "$error"
done
# restore the app data file to a sane selection afterwards
printf '%s' '{"Watched":[{"Code":"510300","AlertEnabled":false,"PremiumThreshold":2},{"Code":"513500","AlertEnabled":true,"PremiumThreshold":2}],"SelectedCode":"510300","Topmost":false,"Compact":false,"KlinePeriod":"Daily","RangeYears":1,"Left":NaN,"Top":NaN}' \
  > "$ROOT/.tools/xdg/data/little-tools/stock/settings.json"

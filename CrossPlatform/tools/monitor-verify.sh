#!/usr/bin/env bash
# One-shot harness for the AI usage monitor port: drives the real X11 windows with
# XTest and asserts what the user can see (window flags, geometry, the settings
# round trip, the drag/edge-hide cycle and the tray switch).
#
#   monitor-verify.sh [outdir]     default: .tools/out/monitor-verify
#
# It starts the workspace build through monitorctl.sh (isolated XDG, short isolated
# TMPDIR) with all three providers on and Manual keys, so the run uses this
# machine's real keyring keys unless you seed monitorctl's providers.json first.
# Every step prints PASS/FAIL; the exit code is the number of failures.
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="${1:-$WORKSPACE_ROOT/.tools/out/monitor-verify}"
export DISPLAY="${DISPLAY:-:1}"
TOOLS="$WORKSPACE_ROOT/CrossPlatform/tools"
X11="$WORKSPACE_ROOT/.tools/x11tool"
RAISE="$WORKSPACE_ROOT/.tools/xraisetool"
mkdir -p "$OUT"
failures=0

pass() { echo "PASS $1: $2"; }
fail() { echo "FAIL $1: $2"; failures=$((failures + 1)); }
expect_eq() { # name expected actual
  if [[ "$2" == "$3" ]]; then pass "$1" "$3"; else fail "$1" "expected=$2 actual=$3"; fi
}
expect_contains() { # name haystack needle
  if [[ "$2" == *"$3"* ]]; then pass "$1" "contains $3"; else fail "$1" "missing '$3' in: $2"; fi
}

main_pid() { bash "$TOOLS/monitorctl.sh" pid 2>/dev/null; }
win_id() { xwininfo -root -tree 2>/dev/null | grep -F "$1" | grep -o '0x[0-9a-f]*' | head -1; }
geom_x() { xwininfo -id "$1" 2>/dev/null | awk '/Absolute upper-left X/{print $NF}'; }
geom_y() { xwininfo -id "$1" 2>/dev/null | awk '/Absolute upper-left Y/{print $NF}'; }
win_size() { xwininfo -id "$1" 2>/dev/null | awk '/Width:/{w=$NF} /Height:/{h=$NF} END{print w"x"h}'; }
raise_win() { "$RAISE" "$1" >/dev/null 2>&1; sleep 0.5; }
# A click needs the window raised first: Mutter hands a synthetic press to a
# skip-taskbar window that is not active inconsistently.
click_win() { # win x y
  raise_win "$1"
  "$X11" click "$2" "$3" >/dev/null
  sleep 1.2
}
shot() { xwd -id "$1" -silent > "$OUT/$2.xwd" 2>/dev/null && python3 "$TOOLS/xwd2png.py" "$OUT/$2.xwd" "$OUT/$2.png" >/dev/null; }

echo "== monitor-verify: $OUT =="
bash "$TOOLS/monitorctl.sh" stop >/dev/null 2>&1
# The switches are persisted, so a previous run's edits (for example a provider
# turned off by the settings step) must not decide this run.
bash "$TOOLS/monitorctl.sh" reset >/dev/null
bash "$TOOLS/monitorctl.sh" start >/dev/null
sleep 6
PID="$(main_pid)"
[[ -n "$PID" ]] || { echo "FAIL start: no pid"; exit 1; }
echo "app pid=$PID"

# ---------------------------------------------------------------- HUD on start
HUD="$(win_id 'AI 余量监控"')"
[[ -n "$HUD" ]] || { echo "FAIL hud: not found"; exit 1; }
STATE="$(xprop -id "$HUD" _NET_WM_STATE)"
expect_contains "hud.size" "$(win_size "$HUD")" "316x92"
expect_contains "hud.above" "$STATE" "_NET_WM_STATE_ABOVE"
expect_contains "hud.skip-taskbar" "$STATE" "_NET_WM_STATE_SKIP_TASKBAR"
expect_eq "hud.top-right" "2226" "$(geom_x "$HUD")"
expect_eq "hud.top" "50" "$(geom_y "$HUD")"
shot "$HUD" "01-hud"

# HUD text: read the snapshot the module itself persisted and require live values.
VALUES="$(bash "$TOOLS/monitorctl.sh" values)"
expect_contains "values.deepseek" "$VALUES" "DeepSeekBalance = ¥"
expect_contains "values.glm" "$VALUES" "GlmBalance = 3.584534445"
expect_contains "values.updated" "$VALUES" "UpdatedAt = /Date("

# --------------------------------------------------------- click -> detail view
click_win "$HUD" "$(( $(geom_x "$HUD") + 158 ))" "$(( $(geom_y "$HUD") + 46 ))"
DETAIL="$(win_id '余量监控明细"')"
[[ -n "$DETAIL" ]] || { fail "details.open" "no detail window"; echo "$failures failures"; exit "$failures"; }
expect_contains "details.size" "$(win_size "$DETAIL")" "380x505"
expect_eq "details.left" "$(( $(geom_x "$HUD") + 316 - 380 ))" "$(geom_x "$DETAIL")"
expect_eq "details.top" "$(( $(geom_y "$HUD") + 92 + 8 ))" "$(geom_y "$DETAIL")"
DETAIL_STATE="$(xprop -id "$DETAIL" _NET_WM_STATE)"
expect_contains "details.above" "$DETAIL_STATE" "_NET_WM_STATE_ABOVE"
expect_contains "details.skip-taskbar" "$DETAIL_STATE" "_NET_WM_STATE_SKIP_TASKBAR"
shot "$DETAIL" "02-details-ds-day"

# Count the series pixels by hue inside the chart area (below the range buttons at
# y~322..342). A fresh live history may only hold a couple of samples, so the
# assertion is "the right colour is there and the other provider's is gone" rather
# than a pixel count threshold.
series_pixels() { # png
  python3 - "$1" <<'PY'
import sys
from PIL import Image
im = Image.open(sys.argv[1]).convert('RGB')
green = purple = 0
for x in range(55, 360):
    for y in range(355, 435):
        r, g, b = im.getpixel((x, y))
        if g - r >= 6 and g - b >= 6:
            green += 1
        elif r - g >= 6 and b - g >= 6:
            purple += 1
print(f"{green} {purple}")
PY
}
read -r ds_green ds_purple <<<"$(series_pixels "$OUT/02-details-ds-day.png")"
if [[ "$ds_green" -gt 0 && "$ds_purple" -eq 0 ]]; then
  pass "details.ds-series" "green=$ds_green purple=$ds_purple"
else
  fail "details.ds-series" "green=$ds_green purple=$ds_purple"
fi

# Switch the trend to GLM + week and capture again: the drawn series changes colour
# (purple) and the title names the provider and range.
# The footer buttons are located from the window height: the detail window
# shrinks when a provider section collapses.
footer_y() { xwininfo -id "$1" | awk '/Absolute upper-left Y/{y=$NF} /Height:/{h=$NF} END{print y + h - 21}'; }
DX="$(geom_x "$DETAIL")"; DY="$(geom_y "$DETAIL")"
click_win "$DETAIL" "$(( DX + 225 ))" "$(( DY + 331 ))"   # GLM
click_win "$DETAIL" "$(( DX + 300 ))" "$(( DY + 331 ))"   # 周
shot "$DETAIL" "03-details-glm-week"
read -r glm_green glm_purple <<<"$(series_pixels "$OUT/03-details-glm-week.png")"
if [[ "$glm_purple" -gt 0 && "$glm_green" -eq 0 ]]; then
  pass "details.glm-series" "purple=$glm_purple green=$glm_green"
else
  fail "details.glm-series" "purple=$glm_purple green=$glm_green"
fi

# ------------------------------------------------------- refresh button works
BEFORE="$(grep -o 'Date([0-9]*)' <<<"$(bash "$TOOLS/monitorctl.sh" values | grep UpdatedAt)")"
click_win "$DETAIL" "$(( DX + 272 ))" "$(footer_y "$DETAIL")"  # 刷新
sleep 2
AFTER="$(grep -o 'Date([0-9]*)' <<<"$(bash "$TOOLS/monitorctl.sh" values | grep UpdatedAt)")"
if [[ "$BEFORE" != "$AFTER" ]]; then pass "details.refresh" "$BEFORE -> $AFTER"; else fail "details.refresh" "UpdatedAt unchanged ($BEFORE)"; fi

# ------------------------------------------------- settings dialog round trip
click_win "$DETAIL" "$(( DX + 197 ))" "$(footer_y "$DETAIL")"  # 设置
SETTINGS="$(win_id '供应商设置"')"
[[ -n "$SETTINGS" ]] || { fail "settings.open" "no dialog"; echo "$failures failures"; exit "$failures"; }
expect_contains "settings.width" "$(win_size "$SETTINGS")" "430x"
SX="$(geom_x "$SETTINGS")"; SY="$(geom_y "$SETTINGS")"
shot "$SETTINGS" "04-settings"
PROVIDERS_BEFORE="$(cat "$WORKSPACE_ROOT/.tools/monitorctl/data/little-tools/monitor/providers.json")"
click_win "$SETTINGS" "$(( SX + 30 ))" "$(( SY + 107 ))"  # 监控 Codex off
click_win "$SETTINGS" "$(( SX + 372 ))" "$(( SY + 480 ))" # 保存
sleep 2
PROVIDERS_AFTER="$(cat "$WORKSPACE_ROOT/.tools/monitorctl/data/little-tools/monitor/providers.json")"
expect_contains "settings.saved" "$PROVIDERS_AFTER" '"CodexEnabled": false'
expect_contains "settings.closed" "$(xwininfo -root -tree 2>/dev/null | grep -c '供应商设置')" "0"
# The module must have collapsed the codex block in the still open detail window.
DETAIL_NOW="$(win_id '余量监控明细"')"
if [[ -n "$DETAIL_NOW" ]]; then
  expect_eq "settings.collapsed-details" "433" "$(xwininfo -id "$DETAIL_NOW" | awk '/Height:/{print $NF}')"
  shot "$DETAIL_NOW" "05-details-codex-off"
fi
# Put Codex back the same way a user would (open the dialog again, tick it, save).
click_win "$DETAIL_NOW" "$(( $(geom_x "$DETAIL_NOW") + 197 ))" "$(footer_y "$DETAIL_NOW")"
SETTINGS2="$(win_id '供应商设置"')"
if [[ -n "$SETTINGS2" ]]; then
  SX2="$(geom_x "$SETTINGS2")"; SY2="$(geom_y "$SETTINGS2")"
  click_win "$SETTINGS2" "$(( SX2 + 30 ))" "$(( SY2 + 107 ))"
  click_win "$SETTINGS2" "$(( SX2 + 372 ))" "$(( SY2 + 480 ))"
  sleep 2
fi
expect_contains "settings.restored" "$(cat "$WORKSPACE_ROOT/.tools/monitorctl/data/little-tools/monitor/providers.json")" '"CodexEnabled": true'
echo "providers.before=$PROVIDERS_BEFORE"
echo "providers.after =$PROVIDERS_AFTER"

# ------------------------------------------------------------- drag + edge hide
HUD="$(win_id 'AI 余量监控"')"
# Close the detail window (a click toggles it) and wait for it to really be gone:
# an open detail window changes the snap path (it reveals instead of hiding).
for _ in 1 2 3; do
  [[ -z "$(win_id '余量监控明细"')" ]] && break
  click_win "$HUD" "$(( $(geom_x "$HUD") + 158 ))" "$(( $(geom_y "$HUD") + 46 ))"
done
expect_eq "drag.details-closed" "0" "$(xwininfo -root -tree 2>/dev/null | grep -c '余量监控明细')"
raise_win "$HUD"
"$X11" drag "$(( $(geom_x "$HUD") + 74 ))" "$(( $(geom_y "$HUD") + 30 ))" 2540 300 0 >/dev/null  # hold=0 releases!
sleep 2
expect_eq "drag.snapped-to-strip" "2551" "$(geom_x "$HUD")"
"$X11" move 2555 "$(( $(geom_y "$HUD") + 46 ))" >/dev/null; sleep 1.2
expect_eq "drag.revealed" "2244" "$(geom_x "$HUD")"
shot "$HUD" "06-hud-revealed"
"$X11" move 1500 900 >/dev/null; sleep 1.8
expect_eq "drag.hidden-again" "2551" "$(geom_x "$HUD")"
raise_win "$HUD"
"$X11" drag 2555 "$(( $(geom_y "$HUD") + 46 ))" 2300 300 0 >/dev/null; sleep 1.5   # pull it back out

# ------------------------------------------------------------- tray on and off
ITEM_STATE() { bash "$TOOLS/trayctl.sh" layout "$PID" 2>/dev/null | tr ',' '\n' | grep -A4 "余量监控" | grep -o 'toggle-state.*'; }
expect_contains "tray.checked" "$(ITEM_STATE)" "1"
bash "$TOOLS/trayctl.sh" click "$PID" 余量监控 >/dev/null; sleep 2
expect_contains "tray.manager-off" "$(grep MonitorEnabled "$WORKSPACE_ROOT/.tools/monitorctl/config/little-tools/manager.json")" "false"
expect_eq "tray.hud-gone" "0" "$(xwininfo -root -tree 2>/dev/null | grep -c 'AI 余量监控')"
expect_contains "tray.unchecked" "$(ITEM_STATE)" "0"
bash "$TOOLS/trayctl.sh" click "$PID" 余量监控 >/dev/null; sleep 4
expect_contains "tray.manager-on" "$(grep MonitorEnabled "$WORKSPACE_ROOT/.tools/monitorctl/config/little-tools/manager.json")" "true"
if [[ -n "$(win_id 'AI 余量监控"')" ]]; then pass "tray.hud-back" "window present"; else fail "tray.hud-back" "no window"; fi
shot "$(win_id 'AI 余量监控"')" "07-hud-after-tray-cycle"

# One last live check: the tray cycle must not have lost the persisted state.
expect_contains "values.after-cycle" "$(bash "$TOOLS/monitorctl.sh" values)" "GlmBalance = 3.584534445"

echo
echo "== $failures failure(s); renders and captures in $OUT =="
bash "$TOOLS/monitorctl.sh" stop >/dev/null 2>&1
exit "$failures"

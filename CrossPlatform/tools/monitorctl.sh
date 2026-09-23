#!/usr/bin/env bash
# Dev-only helper: run the *workspace build* of the suite with the AI usage monitor
# module enabled, in an isolated XDG root and an isolated TMPDIR, so it neither
# touches the user's live install nor collides with one through the single-instance
# command pipe.
#
#   monitorctl.sh start [extra app args]   launch with the monitor switched on
#   monitorctl.sh stop                     --exit through the pipe, then by pid
#   monitorctl.sh pid                      the running app pid
#   monitorctl.sh env                      show the isolated roots
#   monitorctl.sh windows                  list the monitor windows on :1
#   monitorctl.sh values                   print the live snapshot fields
#   monitorctl.sh reset                    write the default manager/providers files
#   monitorctl.sh manager <json>           write manager.json
#   monitorctl.sh provider <json>          write the monitor's providers.json
#
# TMPDIR note (important on this workspace): .NET's Unix named pipes live in
# $TMPDIR/.corefxpipe_<random>, and an AF_UNIX path is limited to about 108
# characters. The workspace path is already ~51 characters, so a TMPDIR under
# .tools/ (for example .tools/stkverify/tmp) pushes the socket path over the limit
# and every pipe - including the suite's own command pipe - silently stops working.
# This helper therefore defaults TMPDIR to a short /tmp path; override it with
# LITTLETOOLS_MONITOR_TMPDIR if you need another one (keep it short).
set -uo pipefail
WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"
ROOT="$WORKSPACE_ROOT/.tools/monitorctl"
export XDG_CONFIG_HOME="$ROOT/config" XDG_DATA_HOME="$ROOT/data" XDG_CACHE_HOME="$ROOT/cache"
export TMPDIR="${LITTLETOOLS_MONITOR_TMPDIR:-/tmp/littletools-monitor-verify}"
export DISPLAY="${DISPLAY:-:1}"
export AVALONIA_TELEMETRY_OPTOUT=1
APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
MANAGER="$XDG_CONFIG_HOME/little-tools/manager.json"
PROVIDERS="$XDG_DATA_HOME/little-tools/monitor/providers.json"
SNAPSHOT="$XDG_DATA_HOME/little-tools/monitor/snapshot.json"
LOG="$ROOT/app.log"
PIDFILE="$ROOT/app.pid"

default_manager() {
  printf '%s\n' '{"MonitorEnabled":true,"TranslateEnabled":true,"TodoNotesEnabled":false,"StockEnabled":false,"EdgeHideMonitor":true,"EdgeHideTranslate":false,"EdgeHideTodo":false,"EdgeHideStock":false}'
}
# Manual key source: the suite's platform secret store (GNOME keyring, then
# credentials.json) is what a Linux user configures through the settings dialog.
default_providers() {
  printf '%s\n' '{"CodexEnabled":true,"DeepSeekEnabled":true,"GlmEnabled":true,"DeepSeekSource":"Manual","DeepSeekEnvironment":"DEEPSEEK_API_KEY","DeepSeekProtectedKey":null,"GlmSource":"Manual","GlmEnvironment":"ZHIPUAI_API_KEY","GlmProtectedKey":null}'
}

case "${1:-}" in
  start)
    shift || true
    mkdir -p "$(dirname "$MANAGER")" "$(dirname "$PROVIDERS")" "$XDG_CACHE_HOME" "$TMPDIR"
    [[ -f "$MANAGER" ]] || default_manager > "$MANAGER"
    [[ -f "$PROVIDERS" ]] || default_providers > "$PROVIDERS"
    : > "$LOG"
    setsid "$DOTNET_ROOT/dotnet" "$APP" "$@" >>"$LOG" 2>&1 < /dev/null &
    app_pid="$!"
    echo "$app_pid" > "$PIDFILE"
    for _ in $(seq 1 24); do
      sleep 0.5
      kill -0 "$app_pid" 2>/dev/null || break
      DISPLAY="$DISPLAY" xwininfo -root -tree 2>/dev/null | grep -q "AI 余量监控" && break
    done
    echo "$app_pid"
    ;;
  stop)
    "$DOTNET_ROOT/dotnet" "$APP" --exit >/dev/null 2>&1 || true
    sleep 1.5
    [[ -f "$PIDFILE" ]] && kill "$(cat "$PIDFILE")" 2>/dev/null
    rm -f "$PIDFILE"
    echo stopped
    ;;
  pid) cat "$PIDFILE" 2>/dev/null ;;
  env)
    echo "XDG_CONFIG_HOME=$XDG_CONFIG_HOME"
    echo "XDG_DATA_HOME=$XDG_DATA_HOME"
    echo "TMPDIR=$TMPDIR"
    echo "manager=$MANAGER"
    echo "providers=$PROVIDERS"
    echo "snapshot=$SNAPSHOT"
    ;;
  reset)
    mkdir -p "$(dirname "$MANAGER")" "$(dirname "$PROVIDERS")"
    default_manager > "$MANAGER"
    default_providers > "$PROVIDERS"
    echo "reset $MANAGER and $PROVIDERS"
    ;;
  manager) mkdir -p "$(dirname "$MANAGER")"; printf '%s\n' "$2" > "$MANAGER" ;;
  provider) mkdir -p "$(dirname "$PROVIDERS")"; printf '%s\n' "$2" > "$PROVIDERS" ;;
  windows) DISPLAY="$DISPLAY" xwininfo -root -tree 2>/dev/null | grep -E "AI 余量监控" ;;
  values)
    python3 - "$SNAPSHOT" <<'PY'
import json, sys
path = sys.argv[1]
try:
    data = json.load(open(path))
except Exception as error:
    print("no snapshot: " + str(error))
    raise SystemExit(1)
for key in ("CodexState", "FiveHourRemaining", "WeeklyRemaining", "PlanType", "CodexCredits",
            "DeepSeekState", "DeepSeekBalance", "TodayDeepSeekSpend",
            "GlmState", "GlmBalance", "GlmTotalSpend", "GlmFiveHourRemaining", "GlmWeeklyRemaining",
            "UpdatedAt"):
    print(f"{key} = {data.get(key)}")
PY
    ;;
  *) echo "usage: $0 start [args]|stop|pid|env|reset|manager <json>|provider <json>|windows|values"; exit 2 ;;
esac

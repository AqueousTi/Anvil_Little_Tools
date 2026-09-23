#!/usr/bin/env bash
# Local test helper (dev-only): drive the suite's StatusNotifierItem tray menu
# over the session bus exactly the way a panel (GNOME AppIndicator host) does:
# read the exported com.canonical.dbusmenu layout and send Event("clicked").
#
#   trayctl.sh service <pid>          status notifier bus name for a pid ("" if none)
#   trayctl.sh menu <pid>             dbusmenu object path
#   trayctl.sh layout <pid>           raw GetLayout dump
#   trayctl.sh items <pid>            "id<TAB>toggle-state<TAB>enabled<TAB>label" per item
#   trayctl.sh click <pid> <label>    send Event(clicked) to the item whose label matches
set -uo pipefail
WATCHER=org.kde.StatusNotifierWatcher
WPATH=/StatusNotifierWatcher
BUS=org.freedesktop.DBus
BPATH=/org/freedesktop/DBus

sni_for_pid() {
  local pid="$1" raw name
  raw="$(gdbus call --session --dest "$WATCHER" --object-path "$WPATH" \
      --method org.freedesktop.DBus.Properties.Get "$WATCHER" RegisteredStatusNotifierItems 2>/dev/null)"
  for name in $(echo "$raw" | grep -oE "'[^']+'" | tr -d "'"); do
    name="${name%%@*}"
    [[ "$name" == org.kde.StatusNotifierItem-* ]] || continue
    local owner pid2
    owner="$(gdbus call --session --dest "$BUS" --object-path "$BPATH" \
        --method org.freedesktop.DBus.GetNameOwner "$name" 2>/dev/null | grep -oE ':[0-9]+\.[0-9]+' | head -1)"
    [[ -n "$owner" ]] || continue
    pid2="$(gdbus call --session --dest "$BUS" --object-path "$BPATH" \
        --method org.freedesktop.DBus.GetConnectionUnixProcessID "$owner" 2>/dev/null | awk '{print $2}' | tr -d ',)')"
    if [[ "$pid2" == "$pid" ]]; then echo "$name"; return 0; fi
  done
  return 1
}

menu_of() {
  gdbus call --session --dest "$1" --object-path /StatusNotifierItem \
    --method org.freedesktop.DBus.Properties.Get org.kde.StatusNotifierItem Menu 2>/dev/null \
    | grep -oE "/net/avaloniaui/dbusmenu/[0-9a-f]+" | head -1
}

layout_of() {
  gdbus call --session --dest "$1" --object-path "$2" \
    --method com.canonical.dbusmenu.GetLayout 0 2 "[]" 2>/dev/null
}

cmd="${1:-}"; pid="${2:-}"; shift 2 2>/dev/null || true
svc="$(sni_for_pid "$pid")" || true
case "$cmd" in
  service) echo "$svc" ;;
  menu)    [[ -n "$svc" ]] && menu_of "$svc" ;;
  layout)  [[ -n "$svc" ]] && layout_of "$svc" "$(menu_of "$svc")" ;;
  items)
    [[ -z "$svc" ]] && exit 1
    layout_of "$svc" "$(menu_of "$svc")" | python3 -c '
import re,sys
s=sys.stdin.read()
# tuples look like (id, {"label": <"股票观察">, "enabled": <true>, "toggle-state": <1>}, [children])
for m in re.finditer(r"\((\d+), \{(.*?)\}, @?av \[", s):
    props=m.group(2)
    label=re.search(r"\x27label\x27: <\x27?(.*?)\x27?>", props)
    label=label.group(1) if label else ""
    if label=="":
        # separator: no label property
        label="<separator>"
    ts=re.search(r"\x27toggle-state\x27: <(\d+)>", props)
    en=re.search(r"\x27enabled\x27: <(true|false)>", props)
    print(f"{m.group(1)}\t{ts.group(1) if ts else chr(45)}\t{en.group(1) if en else chr(45)}\t{label}")
'
    ;;
  click)
    label="$1"
    [[ -z "$svc" ]] && { echo "no tray service for pid $pid"; exit 1; }
    id="$(bash "$0" items "$pid" | awk -F'\t' -v l="$label" '$4==l {print $1; exit}')"
    [[ -z "$id" ]] && { echo "no menu item labelled $label"; bash "$0" items "$pid"; exit 1; }
    gdbus call --session --dest "$svc" --object-path "$(menu_of "$svc")" \
      --method com.canonical.dbusmenu.Event "$id" clicked "<0>" 0
    echo "clicked id=$id label=$label"
    ;;
  *) echo "usage: $0 service|menu|layout|items|click <pid> [label]"; exit 2 ;;
esac

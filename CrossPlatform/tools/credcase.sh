#!/usr/bin/env bash
# Local test helper (dev-only): open the real settings dialog in a controlled
# launch environment so the Baidu credential resolution can be compared between
# a terminal launch and an autostart-like one.
#
#   credcase.sh <case> <outdir> [--keep]
#     case = real      : full PATH + DBUS session bus (a normal terminal launch)
#            nodbus    : full PATH, DBUS_SESSION_BUS_ADDRESS / XDG_RUNTIME_DIR removed
#            nosectool : secret-tool not on PATH (libsecret-tools not installed)
#            minimal   : env -i style: no DISPLAY besides :1, no DBUS, PATH without /usr/bin
#
# XDG_* point at <outdir> (seeded from the real ~/.config/little-tools/assistant-settings.json)
# so the run cannot write the real config, but $HOME stays real, so the GNOME
# keyring lookup is the genuine one. TMPDIR is isolated so the CommandPipe cannot
# reach the user's running instance.
set -uo pipefail

WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$WORKSPACE_ROOT/CrossPlatform/tools/env.sh"

case_name="${1:?case}"
out="${2:?outdir}"
keep="${3:-}"

APP="$WORKSPACE_ROOT/CrossPlatform/LittleTools.Assistant/bin/Release/net10.0/LittleTools.Assistant.dll"
LOG="$out/app.log"

rm -rf "$out"
mkdir -p "$out/config/little-tools" "$out/data" "$out/cache" "$out/tmp"
cp "$HOME/.config/little-tools/assistant-settings.json" "$out/config/little-tools/assistant-settings.json"

export DISPLAY=:1
export XDG_CONFIG_HOME="$out/config"
export XDG_DATA_HOME="$out/data"
export XDG_CACHE_HOME="$out/cache"
export TMPDIR="$out/tmp"
export AVALONIA_TELEMETRY_OPTOUT=1
export XMODIFIERS="@im=none"
export GTK_IM_MODULE=gtk-im-context-simple
unset TRANSLATE_APP_CONFIG BAIDU_TRANSLATE_APP_ID BAIDU_TRANSLATE_SECRET_KEY

# The restricted PATH is handed to the app only: the harness itself still needs
# mkdir/sleep/seq/sed, which live in the directories the case removes.
app_path="$PATH"
case "$case_name" in
  real)
    ;;
  nodbus)
    unset DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR
    ;;
  nosectool)
    # /bin and /sbin are symlinks to /usr/bin and /usr/sbin on Ubuntu, so they
    # have to be excluded too or secret-tool is still found through the symlink.
    app_path="/usr/local/sbin:/usr/local/bin:/usr/games"
    ;;
  minimal)
    app_path="/usr/local/bin"
    unset DBUS_SESSION_BUS_ADDRESS XDG_RUNTIME_DIR
    ;;
  configonly)
    # No secret-tool and a pre-seeded credentials.json: this is the "keyring is
    # not installed, the user already saved into the durable file" case, i.e. the
    # state a reinstall has to survive.
    app_path="/usr/local/sbin:/usr/local/bin:/usr/games"
    mkdir -p "$out/config/little-tools/translate"
    printf '{\n  "baidu": "yoTPcm5VXUrhFM89oQrh"\n}\n' > "$out/config/little-tools/translate/credentials.json"
    chmod 600 "$out/config/little-tools/translate/credentials.json"
    ;;
  legacy)
    # The state right after reinstall-linux.sh parked a program-dir
    # appsettings.json: no keyring, credentials only in the config directory.
    app_path="/usr/local/sbin:/usr/local/bin:/usr/games"
    mkdir -p "$out/config/little-tools/translate"
    printf '{\n  "AppId": "20260623002636616",\n  "SecretKey": "yoTPcm5VXUrhFM89oQrh"\n}\n' \
      > "$out/config/little-tools/translate/appsettings.json"
    chmod 600 "$out/config/little-tools/translate/appsettings.json"
    ;;
  *) echo "unknown case: $case_name" >&2; exit 2 ;;
esac

mkdir -p "$out"
: > "$LOG"
# No setsid: setsid(1) forks and exits, which loses the app's real PID, and the
# script kills the app explicitly at the end anyway.
env PATH="$app_path" "$DOTNET_ROOT/dotnet" "$APP" --translate >>"$LOG" 2>&1 </dev/null &
app_pid=$!
trap '[[ "${keep:-}" == "--keep" ]] || { kill -9 "$app_pid" 2>/dev/null; pkill -9 -P "$app_pid" 2>/dev/null; }' EXIT

win_id() { # $1 = title substring, $2 = expected _NET_WM_PID
  local id p
  while read -r id _; do
    p="$(xprop -id "$id" _NET_WM_PID 2>/dev/null | grep -oE '[0-9]+$')"
    [[ "$p" == "$2" ]] || continue
    echo "$id"; return 0
  done < <(xwininfo -root -tree 2>/dev/null | grep -F "\"$1\"" | awk '{print $1}')
  return 1
}

# The window must belong to this run, so match the X PID against the launched
# dotnet process instead of trusting the title alone.
sleep 3

wid=""
for _ in $(seq 1 40); do
  wid="$(win_id "Little Tools AI" "$app_pid")"
  [[ -n "$wid" ]] && break
  sleep 0.5
done
if [[ -z "$wid" ]]; then echo "no AI window (case=$case_name)"; sed -n '1,40p' "$LOG"; exit 1; fi

geom="$(xwininfo -id "$wid" | awk '/Absolute upper-left X/{x=$4} /Absolute upper-left Y/{y=$4} /Width:/{w=$2} /Height:/{h=$2} END{print w, h, x, y}')"
read -r w h wx wy <<<"$geom"
echo "case=$case_name window=$wid ${w}x${h}+${wx}+${wy} pid=$app_pid"

$WORKSPACE_ROOT/.tools/xraisetool "$wid" >/dev/null 2>&1
sleep 0.8

# Route button: centre-left of the compact capsule (measured on this 430x80 layout).
rx=$(( wx + w * 133 / 1000 ))
ry=$(( wy + h * 712 / 1000 ))
$WORKSPACE_ROOT/.tools/x11tool click "$rx" "$ry" >/dev/null
sleep 1.2
# The popup is not keyboard focused, so pick the last entry ("翻译设置…") with the
# mouse. Offsets are measured from the capsule on this fixed 14-route menu.
$WORKSPACE_ROOT/.tools/x11tool click $(( wx + 133 )) $(( wy + 426 )) >/dev/null
sleep 2.0

# Crop the settings dialog frame.
line="$(xwininfo -root -tree 2>/dev/null | grep -F '"翻译与问答设置": ("mutter-x11-frames"' | head -1)"
if [[ -z "$line" ]]; then echo "settings dialog did not open (case=$case_name)"; exit 1; fi
python3 - "$line" "$out/settings.png" <<'PY'
import re, subprocess, sys
line, out = sys.argv[1], sys.argv[2]
m = re.search(r'\s(\d+)x(\d+)\+(-?\d+)\+(-?\d+)\s+\+(-?\d+)\+(-?\d+)', line)
w, h, x, y = (int(m.group(i)) for i in range(1, 5))
subprocess.run(["gnome-screenshot", "-f", out + ".shot.png"], check=False,
               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
from PIL import Image
im = Image.open(out + ".shot.png").convert("RGB")
im.crop((x, y, x + w, y + h)).save(out)
print(f"settings {w}x{h}+{x}+{y} -> {out}")
PY

if [[ "$keep" != "--keep" ]]; then
  kill -9 "$app_pid" 2>/dev/null
  pkill -9 -P "$app_pid" 2>/dev/null
fi
echo "done case=$case_name"

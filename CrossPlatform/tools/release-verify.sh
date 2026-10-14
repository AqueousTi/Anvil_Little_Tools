#!/usr/bin/env bash
# End-to-end release check for the Linux tarball, entirely inside a throwaway
# HOME: install over a fake previous install, run the installed copy, upgrade
# while it is running, then uninstall with and without --purge.
#
#   release-verify.sh [--package FILE] [OUTDIR]
#
# It never reads or writes the caller's real HOME (~/.local/share/little-tools,
# ~/.config/little-tools): HOME is a fresh directory under OUTDIR, TMPDIR is the
# short path /tmp/lt-release-verify-tmp (the .NET named pipes live in TMPDIR and
# AF_UNIX paths are limited to about 108 characters, so the deep workspace path
# would silently break them), and XDG_* are unset so the XDG defaults inside that
# HOME apply.
#
# A running real installation is not disturbed: the isolated TMPDIR means the
# sandboxed copy cannot see the real command pipe. Global hotkeys may be reported
# as conflicts while the real suite owns the X grab - that is expected and does
# not affect any check here.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/CrossPlatform/tools/env.sh"

package=""
outdir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --package) package="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) outdir="$1"; shift ;;
  esac
done
if [[ -z "$package" ]]; then
  # shellcheck disable=SC2012
  package="$(ls -t "$ROOT"/LittleTools-linux-x64-*.tar.gz 2>/dev/null | head -1)"
fi
if [[ ! -f "$package" ]]; then
  echo "release-verify: no tarball found (pass --package FILE)" >&2
  exit 2
fi
outdir="${outdir:-$ROOT/.tools/out/release-verify}"
sandbox="$outdir/sandbox"
short_tmp="/tmp/lt-release-verify-tmp"
work="$outdir/unpacked"
log="$outdir/run.log"

pass=0
fail=0
check() { # check <label> <status>
  if [[ "$2" -eq 0 ]]; then
    echo "  PASS  $1"
    pass=$((pass + 1))
  else
    echo "  FAIL  $1"
    fail=$((fail + 1))
  fi
}
step() { echo; echo "== $*"; }

rm -rf "$outdir" "$short_tmp"
mkdir -p "$sandbox" "$short_tmp" "$work"
exec > >(tee "$log") 2>&1

echo "package: $(readlink -f "$package")"
echo "sandbox HOME: $sandbox (real HOME is not used)"
echo "isolated TMPDIR: $short_tmp"

step "unpack"
tar -xzf "$package" -C "$work"
pkgdir="$(ls -d "$work"/LittleTools-linux-x64-* 2>/dev/null | head -1)"
check "tarball unpacks to LittleTools-linux-x64-*" $?
[[ -n "$pkgdir" ]] || { echo "nothing to verify"; exit 1; }
version="$(head -1 "$pkgdir/VERSION")"
echo "package version: $version"
check "package carries a VERSION file" $([[ -n "$version" ]] && echo 0 || echo 1)
check "package carries BUILD-INFO.txt" $([[ -s "$pkgdir/BUILD-INFO.txt" ]] && echo 0 || echo 1)
check "package ships the user guide as README.md" $([[ -s "$pkgdir/README.md" ]] && echo 0 || echo 1)
check "package ships the developer README and the handover document" \
  "$([[ -s "$pkgdir/DEVELOPER-README.md" && -s "$pkgdir/HANDOVER.md" ]] && echo 0 || echo 1)"

export HOME="$sandbox" TMPDIR="$short_tmp" DISPLAY="${DISPLAY:-:1}"
unset XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME
export XMODIFIERS=@im=none
app="$HOME/.local/share/little-tools/app/LittleTools.Assistant"
lt="$HOME/.local/share/little-tools/bin/little-tools"

step "seed existing configuration and data, plus a fake previous install"
mkdir -p "$HOME/.config/little-tools" "$HOME/.local/share/little-tools/todo" \
         "$HOME/.local/share/little-tools/app"
printf '{"BaiduAppId":"seed-appid","SecretKey":"seed-secret"}\n' > "$HOME/.config/little-tools/assistant-settings.json"
printf '{"AssistantEnabled":true,"TodoEnabled":false,"StockEnabled":false,"MonitorEnabled":false}\n' > "$HOME/.config/little-tools/manager.json"
printf '{"Version":1,"Days":[]}\n' > "$HOME/.local/share/little-tools/todo/data.json"
cat > "$HOME/.local/share/little-tools/app/LittleTools.Assistant" <<'FAKE'
#!/bin/sh
echo "previous-install invoked: $*" >> "$LT_EXIT_LOG"
FAKE
chmod +x "$HOME/.local/share/little-tools/app/LittleTools.Assistant"
printf '{"BaiduAppId":"legacy-appid","SecretKey":"legacy-secret"}\n' > "$HOME/.local/share/little-tools/app/appsettings.json"
export LT_EXIT_LOG="$outdir/previous-install.log"
seed="$outdir/seed.md5"
# The program directory is replaced on purpose by every install; compare the user
# files around it byte for byte.
user_files() { ( cd "$HOME" && find .config .local -type f | grep -v '/little-tools/app/' | sort | xargs md5sum ); }
user_files > "$seed"

step "install (--autostart)"
bash "$pkgdir/install.sh" --autostart > /dev/null
check "installer exits 0" $?
check "installer asked the previous copy to quit (--exit)" \
  "$(grep -q -- '--exit' "$outdir/previous-install.log" 2>/dev/null && echo 0 || echo 1)"
check "legacy appsettings.json was rescued into the XDG config directory" \
  "$([[ -f "$HOME/.config/little-tools/translate/appsettings.json" ]] && echo 0 || echo 1)"
check "rescued credential file is 0600" \
  "$([[ "$(stat -c %a "$HOME/.config/little-tools/translate/appsettings.json" 2>/dev/null)" == "600" ]] && echo 0 || echo 1)"
check "installed release number matches the package" \
  "$([[ "$(cat "$HOME/.local/share/little-tools/VERSION" 2>/dev/null)" == "$version" ]] && echo 0 || echo 1)"
check "launcher symlink points at the app" \
  "$([[ "$(readlink "$lt" 2>/dev/null)" == "$app" ]] && echo 0 || echo 1)"
check "desktop entry installed" $([[ -f "$HOME/.local/share/applications/little-tools.desktop" ]] && echo 0 || echo 1)
check "autostart entry installed (--autostart)" $([[ -f "$HOME/.config/autostart/little-tools.desktop" ]] && echo 0 || echo 1)
( cd "$HOME" && md5sum -c "$seed" > /dev/null 2>&1 )
check "seeded user config/data byte-identical after install" $?

step "--version and --diagnose of the installed copy"
got="$("$lt" --version 2>/dev/null)"
check "--version exits 0" $?
check "--version reports the package version ($got)" \
  "$([[ "$got" == "$version"* ]] && echo 0 || echo 1)"
rm -f "$outdir/diagnose-idle.json"
"$lt" --diagnose "$outdir/diagnose-idle.json" > /dev/null 2>&1
check "--diagnose exits 0" $?
python3 - "$outdir/diagnose-idle.json" "$version" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
assert d.get("session"), d
assert d.get("buildVersion", "").startswith(sys.argv[2]), d.get("buildVersion")
assert d.get("configDirectory", "").startswith(sys.argv[1].rsplit("/", 1)[0]) or d.get("configDirectory")
print("  diagnose:", d.get("session"), d.get("buildVersion"), "tray=" + str(d.get("trayCreated")))
PY
check "--diagnose JSON has session, matching buildVersion and paths" $?

step "start --background, diagnose the running instance, --exit"
setsid nohup "$app" --background > "$outdir/app.log" 2>&1 < /dev/null &
sleep 10
pid="$(pgrep -f "$app --background" | head -1)"
check "background instance is running" $([[ -n "$pid" ]] && echo 0 || echo 1)
rm -f "$outdir/diagnose-running.json"
"$lt" --diagnose "$outdir/diagnose-running.json" > /dev/null 2>&1
check "--diagnose against the running instance exits 0" $?
python3 - "$outdir/diagnose-running.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
assert d.get("peerSameBuild") is True, d
print("  peer:", d.get("peerVersion"))
PY
check "headless diagnose reports the peer as the same build" $?
"$lt" --exit > /dev/null 2>&1
check "--exit exits 0" $?
sleep 3
check "the instance is gone after --exit" "$(pgrep -f "$app --background" > /dev/null && echo 1 || echo 0)"

step "upgrade while an instance of this package is running"
setsid nohup "$app" --background > "$outdir/app2.log" 2>&1 < /dev/null &
sleep 10
old_pid="$(pgrep -f "$app --background" | head -1)"
check "instance running before the upgrade" $([[ -n "$old_pid" ]] && echo 0 || echo 1)
user_files > "$outdir/before-upgrade.md5"
bash "$pkgdir/install.sh" > /dev/null
check "upgrade installs 0" $?
sleep 3
check "the pre-upgrade instance was replaced (no process left)" \
  "$(pgrep -f "$app --background" > /dev/null && echo 1 || echo 0)"
check "no .app.staging/.app.previous leftovers" \
  "$(ls -a "$HOME/.local/share/little-tools" | grep -qE '^\.app\.' && echo 1 || echo 0)"
( cd "$HOME" && md5sum -c "$outdir/before-upgrade.md5" > /dev/null 2>&1 )
check "user config/data byte-identical after the upgrade" $?

step "the upgraded copy runs, then uninstall keeps user data"
setsid nohup "$app" --background > "$outdir/app3.log" 2>&1 < /dev/null &
sleep 10
check "upgraded copy starts" "$(pgrep -f "$app --background" > /dev/null && echo 0 || echo 1)"
bash "$pkgdir/uninstall.sh" > /dev/null
check "uninstall exits 0" $?
check "program directory removed" $([[ ! -e "$HOME/.local/share/little-tools/app" ]] && echo 0 || echo 1)
check "launcher removed" $([[ ! -e "$lt" ]] && echo 0 || echo 1)
check "desktop entry removed" $([[ ! -e "$HOME/.local/share/applications/little-tools.desktop" ]] && echo 0 || echo 1)
check "autostart entry removed" $([[ ! -e "$HOME/.config/autostart/little-tools.desktop" ]] && echo 0 || echo 1)
check "VERSION removed" $([[ ! -e "$HOME/.local/share/little-tools/VERSION" ]] && echo 0 || echo 1)
check "todo data kept" $([[ -f "$HOME/.local/share/little-tools/todo/data.json" ]] && echo 0 || echo 1)
check "config kept" $([[ -f "$HOME/.config/little-tools/manager.json" ]] && echo 0 || echo 1)
check "the uninstalled instance was stopped" "$(pgrep -f "$app --background" > /dev/null && echo 1 || echo 0)"

step "uninstall --purge removes user data"
bash "$pkgdir/uninstall.sh" --purge > /dev/null
check "uninstall --purge exits 0" $?
check "config directory removed" $([[ ! -e "$HOME/.config/little-tools" ]] && echo 0 || echo 1)
check "data directory removed" $([[ ! -e "$HOME/.local/share/little-tools" ]] && echo 0 || echo 1)

echo
echo "release-verify: pass=$pass fail=$fail"
echo "log: $log"
[[ $fail -eq 0 ]]

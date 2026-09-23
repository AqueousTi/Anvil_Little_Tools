#!/usr/bin/env bash
# Rebuild, repackage, replace the installed copy and restart the suite.
# Every linux-port update goes through this so the user never tests a stale build.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/CrossPlatform/tools/env.sh"

install_dir="$HOME/.local/share/little-tools/app"
bin_link="$HOME/.local/share/little-tools/bin/little-tools"

echo "== build + package =="
"$root/CrossPlatform/build-linux.sh" --publish >/dev/null
rm -f "$root"/LittleTools-linux-x64-*.tar.gz
archive="$("$root/CrossPlatform/package-linux.sh" | tail -1)"
package="$(basename "$archive" .tar.gz)"
echo "package: $package"

echo "== unpack =="
stage="$root/.tools/reinstall-stage"
rm -rf "$stage" && mkdir -p "$stage"
tar -xzf "$archive" -C "$stage"

# The program directory is replaced wholesale below, so a credential that only
# exists in its legacy appsettings.json would be deleted by this update. Park it
# in the XDG config directory first; BaiduCredentials.ReadLegacy reads it from
# there, and saving in the settings dialog migrates it to the keyring or to
# translate/credentials.json.
backup_legacy_credentials() {
  local dir="$1" destination backup
  [[ -f "$dir/appsettings.json" ]] || return 0
  destination="${XDG_CONFIG_HOME:-$HOME/.config}/little-tools/translate"
  mkdir -p "$destination"
  backup="$destination/appsettings.backup-$(date +%Y%m%d-%H%M%S).json"
  cp -p "$dir/appsettings.json" "$backup"
  cp -p "$dir/appsettings.json" "$destination/appsettings.json"
  chmod 600 "$backup" "$destination/appsettings.json" 2>/dev/null || true
  echo
  echo "检测到程序目录里的 appsettings.json（旧版百度凭据），已备份："
  echo "  $backup"
  echo "  $destination/appsettings.json"
  echo "程序仍会读取它；在设置界面点一次“保存”即可迁移到系统钥匙串或"
  echo "credentials.json。更推荐直接用环境变量 BAIDU_TRANSLATE_APP_ID /"
  echo "BAIDU_TRANSLATE_SECRET_KEY。"
  echo
}

echo "== remove the previous install =="
backup_legacy_credentials "$install_dir"
rm -rf "$install_dir"
rm -f "$bin_link"
# Older packages could leave other copies lying around; drop them too.
find "$HOME/.local/share/little-tools" -maxdepth 1 -mindepth 1 -name 'app*' -exec rm -rf {} + 2>/dev/null || true

echo "== install =="
env -u XDG_DATA_HOME -u XDG_CONFIG_HOME -u XDG_CACHE_HOME \
    bash "$stage/$package/install.sh" --autostart >/dev/null

echo "== restart =="
for pid in $(ps -eo pid,comm --no-headers | grep -i littletools | awk '{print $1}'); do
    kill -9 "$pid" 2>/dev/null || true
done
sleep 2
export DISPLAY="${DISPLAY:-:1}"
# The restarted instance must see the same environment the installer used. If the
# caller's shell exports XDG_CONFIG_HOME (exactly what this repository's Linux test
# environment does), the background app would otherwise read *that* config
# directory, find no assistant-settings.json and look unconfigured -- which is what
# "every update asks me for the Baidu API key again" looked like.
env -u XDG_DATA_HOME -u XDG_CONFIG_HOME -u XDG_CACHE_HOME \
    setsid nohup "$install_dir/LittleTools.Assistant" --background >/dev/null 2>&1 </dev/null &
sleep 8

rm -rf "$stage"
echo "installed: $(sha256sum "$install_dir/LittleTools.Assistant" | cut -c1-16)"
echo "commit:    $(git -C "$root" rev-parse --short HEAD)"
echo "windows:   $(DISPLAY="${DISPLAY:-:1}" xwininfo -root -tree 2>/dev/null | grep -c 'Daily Todo') todo window(s)"

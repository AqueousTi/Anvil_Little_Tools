#!/usr/bin/env bash
# Rebuild, repackage, replace the installed copy and restart the suite.
# Every linux-port update goes through this so the user never tests a stale build.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$root/.tools/env.sh"

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

echo "== remove the previous install =="
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
setsid nohup "$install_dir/LittleTools.Assistant" --background >/dev/null 2>&1 </dev/null &
sleep 8

rm -rf "$stage"
echo "installed: $(sha256sum "$install_dir/LittleTools.Assistant" | cut -c1-16)"
echo "commit:    $(git -C "$root" rev-parse --short HEAD)"
echo "windows:   $(DISPLAY="${DISPLAY:-:1}" xwininfo -root -tree 2>/dev/null | grep -c 'Daily Todo') todo window(s)"

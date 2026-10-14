#!/usr/bin/env bash
# Removes an installed Little Tools suite host.
#
# User data and configuration are kept on purpose: uninstalling the program must
# never delete todos, watched stocks or API credentials. Pass --purge to remove
# them as well.
set -euo pipefail

data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
install_root="$data_home/little-tools/app"
desktop_file="$data_home/applications/little-tools.desktop"
autostart_file="$config_home/autostart/little-tools.desktop"
launcher="$data_home/little-tools/bin/little-tools"
# Release number written by install-linux.sh next to app/; ours, not user data.
version_file="$data_home/little-tools/VERSION"

purge=0
[[ "${1:-}" == "--purge" ]] && purge=1

if [[ -x "$install_root/LittleTools.Assistant" ]]; then
  "$install_root/LittleTools.Assistant" --autostart-disable >/dev/null 2>&1 || true
  # Stop the running copy as well, so "uninstalled" does not mean "still on the
  # desktop until the next logout". Its own command pipe is used; --exit on a
  # machine with nothing running is a no-op that returns 0.
  "$install_root/LittleTools.Assistant" --exit >/dev/null 2>&1 || true
  sleep 1
fi

rm -f "$launcher" "$desktop_file" "$autostart_file" "$version_file"
rm -rf "$install_root"
rmdir "$data_home/little-tools/bin" 2>/dev/null || true

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$data_home/applications" >/dev/null 2>&1 || true
fi

if [[ $purge -eq 1 ]]; then
  rm -rf "$config_home/little-tools" "$data_home/little-tools"
  echo "Removed Little Tools and its user data."
else
  echo "Removed Little Tools. User data in $config_home/little-tools and $data_home/little-tools was kept."
fi

#!/usr/bin/env bash
set -euo pipefail

script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -f "$script_root/App/LittleTools.Assistant" ]]; then
  app_source="$script_root/App"
else
  app_source="$script_root/artifacts/linux-x64"
fi
install_root="${XDG_DATA_HOME:-$HOME/.local/share}/little-tools/assistant"
desktop_root="${XDG_DATA_HOME:-$HOME/.local/share}/applications"

if [[ ! -x "$app_source/LittleTools.Assistant" ]]; then
  echo "Missing Linux publish output: $app_source/LittleTools.Assistant" >&2
  exit 1
fi

mkdir -p "$install_root" "$desktop_root"
cp -a "$app_source/." "$install_root/"

desktop_file="$desktop_root/little-tools-assistant.desktop"
sed \
  -e "s|@EXEC@|$install_root/LittleTools.Assistant|g" \
  -e "s|@ICON@|$install_root/little-tools.png|g" \
  "$script_root/little-tools-assistant.desktop.in" > "$desktop_file"
chmod +x "$install_root/LittleTools.Assistant"

echo "Installed Little Tools Assistant to $install_root"
echo "For Shift+Backspace on GNOME, create a custom keyboard shortcut that runs:"
echo "  $install_root/LittleTools.Assistant --toggle"

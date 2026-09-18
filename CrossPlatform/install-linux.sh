#!/usr/bin/env bash
# Installs the Little Tools suite host for the current user.
#
#   ./install-linux.sh                 install the app and a desktop entry
#   ./install-linux.sh --autostart     also start it at login (deferred 30s)
#
# The published files are expected in either <script dir>/App (package layout)
# or CrossPlatform/artifacts/linux-x64 (repository build output).
set -euo pipefail

script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -x "$script_root/App/LittleTools.Assistant" ]]; then
  app_source="$script_root/App"
else
  app_source="$script_root/artifacts/linux-x64"
fi

data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
install_root="$data_home/little-tools/app"
bin_root="$data_home/little-tools/bin"
desktop_root="$data_home/applications"

if [[ ! -x "$app_source/LittleTools.Assistant" ]]; then
  echo "Missing Linux publish output: $app_source/LittleTools.Assistant" >&2
  echo "Run ./build-linux.sh --publish first." >&2
  exit 1
fi

# Ask a running instance to quit through its own command pipe. This is safer than
# pattern matching process command lines, which can hit unrelated processes.
if [[ -x "$install_root/LittleTools.Assistant" ]]; then
  "$install_root/LittleTools.Assistant" --exit >/dev/null 2>&1 || true
  sleep 1
fi

mkdir -p "$install_root" "$bin_root" "$desktop_root"
cp -a "$app_source/." "$install_root/"
chmod +x "$install_root/LittleTools.Assistant"

# Stable entry point for desktop shortcuts and terminal use.
ln -sf "$install_root/LittleTools.Assistant" "$bin_root/little-tools"

desktop_file="$desktop_root/little-tools.desktop"
sed \
  -e "s|@EXEC@|$install_root/LittleTools.Assistant|g" \
  -e "s|@ICON@|$install_root/little-tools.png|g" \
  "$script_root/little-tools-assistant.desktop.in" > "$desktop_file"
chmod +x "$desktop_file"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$desktop_root" >/dev/null 2>&1 || true
fi

if [[ "${1:-}" == "--autostart" ]]; then
  "$install_root/LittleTools.Assistant" --autostart-enable >/dev/null
  echo "Autostart enabled (deferred 30s)."
fi

echo "Installed Little Tools to $install_root"
echo "Launch from your application menu, or run: $bin_root/little-tools"
if [[ "$(printenv XDG_SESSION_TYPE 2>/dev/null || true)" == "wayland" ]]; then
  echo
  echo "Wayland does not allow applications to grab global hotkeys. To keep"
  echo "Shift+Backspace / Ctrl+Backspace / Ctrl+Alt+X working, add custom"
  echo "shortcuts in GNOME Settings -> Keyboard -> Custom Shortcuts using:"
  echo "  $bin_root/little-tools --toggle"
fi

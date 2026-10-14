#!/usr/bin/env bash
# Installs or upgrades the Little Tools suite host for the current user.
#
#   ./install-linux.sh                 install the app and a desktop entry
#   ./install-linux.sh --autostart     also start it at login (deferred 30s)
#
# The published files are expected in either <script dir>/App (package layout)
# or CrossPlatform/artifacts/linux-x64 (repository build output).
#
# Upgrading in place is the normal case, so this script is written around three
# rules:
#   1. never touch user data - ~/.config/little-tools, the XDG data directory and
#      the keyring are not read, moved or deleted here;
#   2. replace the program directory atomically (copy to a staging directory next
#      to it, rename, keep the previous one until the rename succeeded), so a
#      failed copy leaves the working install alone;
#   3. rescue the one file users historically kept *inside* the program directory
#      (a legacy appsettings.json with Baidu credentials) into the XDG config
#      directory before the swap; reinstall-linux.sh does the same and the program
#      reads it from there.
set -euo pipefail

script_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ -x "$script_root/App/LittleTools.Assistant" ]]; then
  app_source="$script_root/App"
else
  app_source="$script_root/artifacts/linux-x64"
fi

autostart=0
extra_args=()
for arg in "$@"; do
  case "$arg" in
    --autostart) autostart=1 ;;
    -h|--help)
      echo "usage: install-linux.sh [--autostart]"
      echo "  --autostart   also enable XDG autostart for this user (deferred 30s)"
      exit 0
      ;;
    '') ;;
    *) extra_args+=("$arg") ;;
  esac
done
if [[ ${#extra_args[@]} -gt 0 ]]; then
  echo "install-linux.sh: unknown option '${extra_args[0]}' (supported: --autostart)" >&2
  exit 2
fi

data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
config_home="${XDG_CONFIG_HOME:-$HOME/.config}"
install_root="$data_home/little-tools/app"
install_parent="$(dirname "$install_root")"
bin_root="$data_home/little-tools/bin"
desktop_root="$data_home/applications"

if [[ ! -x "$app_source/LittleTools.Assistant" ]]; then
  echo "Missing Linux publish output: $app_source/LittleTools.Assistant" >&2
  echo "Run ./build-linux.sh --publish first." >&2
  exit 1
fi

# Refuse to install a directory onto itself (for example running install.sh from
# inside the installed copy): the swap below would move the source away.
if [[ -n "$(readlink -f "$install_root" 2>/dev/null || true)" \
   && "$(readlink -f "$app_source")" == "$(readlink -f "$install_root")" ]]; then
  echo "The source directory is the installed copy ($install_root); nothing to do." >&2
  exit 1
fi

version="unknown"
if [[ -f "$script_root/VERSION" ]]; then
  version="$(head -1 "$script_root/VERSION")"
fi

rescue_legacy_credentials() {
  local source="$install_root/appsettings.json" destination backup
  [[ -f "$source" ]] || return 0
  destination="$config_home/little-tools/translate"
  mkdir -p "$destination"
  backup="$destination/appsettings.backup-$(date +%Y%m%d-%H%M%S).json"
  cp -p "$source" "$backup"
  cp -p "$source" "$destination/appsettings.json"
  chmod 600 "$backup" "$destination/appsettings.json" 2>/dev/null || true
  echo
  echo "检测到程序目录里的 appsettings.json（旧版百度凭据），已备份到程序目录之外："
  echo "  $backup"
  echo "  $destination/appsettings.json"
  echo "程序会继续读取它；在设置界面点一次“保存”即可迁移到系统钥匙串或"
  echo "credentials.json。更推荐直接用环境变量 BAIDU_TRANSLATE_APP_ID /"
  echo "BAIDU_TRANSLATE_SECRET_KEY。"
  echo
}

# Ask a running instance to quit through its own command pipe. This is safer than
# pattern matching process command lines, which can hit unrelated processes.
if [[ -x "$install_root/LittleTools.Assistant" ]]; then
  "$install_root/LittleTools.Assistant" --exit >/dev/null 2>&1 || true
  sleep 1
fi

rescue_legacy_credentials

# --- replace the program directory ------------------------------------------
stage="$install_parent/.app.staging.$$"
previous="$install_parent/.app.previous.$$"
for candidate in "$stage" "$previous"; do
  if [[ "$(dirname "$candidate")" != "$install_parent" || "$(basename "$candidate")" != .app.* ]]; then
    echo "Refusing to work with the unexpected path $candidate" >&2
    exit 1
  fi
done
rm -rf "$stage" "$previous"
trap 'if [[ -n "$stage" ]]; then rm -rf "$stage"; fi' EXIT

mkdir -p "$install_parent" "$bin_root" "$desktop_root" "$stage"
cp -a "$app_source/." "$stage/"
chmod +x "$stage/LittleTools.Assistant"

if [[ -e "$install_root" ]]; then
  mv "$install_root" "$previous"
fi
if ! mv "$stage" "$install_root"; then
  echo "install: could not move the new files into place; restoring the previous install" >&2
  if [[ -e "$previous" ]]; then mv "$previous" "$install_root"; fi
  exit 1
fi
rm -rf "$previous"
stage="" # the staging directory was renamed; nothing left for the trap

# The installed release number lives next to app/ so it survives the swap.
printf '%s\n' "$version" > "$install_parent/VERSION"

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

if [[ $autostart -eq 1 ]]; then
  "$install_root/LittleTools.Assistant" --autostart-enable >/dev/null
  echo "Autostart enabled (deferred 30s)."
fi

echo "Installed Little Tools $version to $install_root"
echo "Launch from your application menu, or run: $bin_root/little-tools"
echo "User data was not touched: $config_home/little-tools and $data_home/little-tools"
echo "Check what is running with: $bin_root/little-tools --version"
if [[ "$(printenv XDG_SESSION_TYPE 2>/dev/null || true)" == "wayland" ]]; then
  echo
  echo "Wayland does not allow applications to grab global hotkeys. To keep"
  echo "Shift+Backspace / Ctrl+Backspace / Ctrl+Alt+X working, add custom"
  echo "shortcuts in GNOME Settings -> Keyboard -> Custom Shortcuts using:"
  echo "  $bin_root/little-tools --toggle"
fi

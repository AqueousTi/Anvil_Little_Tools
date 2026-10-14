#!/usr/bin/env bash
# Produces the Linux distribution artefacts from a published build.
#
#   ./package-linux.sh              -> <repo>/LittleTools-linux-x64-<version>-<date>.tar.gz
#   ./package-linux.sh --deb        -> also <repo>/little-tools_<version>_<arch>.deb
#   ./package-linux.sh --version    -> print the release version and exit
#
# The version comes from the published payload itself (the informational version
# string the SDK embeds, i.e. <version>+<git commit>), falling back to <Version>
# in the csproj. Reading it out of the binary with `strings` instead of running
# it matters: `LittleTools.Assistant` without a recognised flag opens the
# translation window and forwards it to a running instance, and a packaging step
# must never pop a window in the user's session. The full runtime identity (which
# also fingerprints the shipped file) is written to BUILD-INFO.txt and can be
# asked of the installed copy with `little-tools --version`.
#
# The tarball layout is the one install-linux.sh/uninstall-linux.sh expect:
#
#   <package>/App/               self contained publish output
#   <package>/install.sh         install-linux.sh
#   <package>/uninstall.sh       uninstall-linux.sh
#   <package>/little-tools-assistant.desktop.in
#   <package>/README.md          user guide (docs/USER-GUIDE.md)
#   <package>/DEVELOPER-README.md  CrossPlatform/README.md
#   <package>/HANDOVER.md        CrossPlatform/HANDOVER.md
#   <package>/VERSION            plain release number
#   <package>/BUILD-INFO.txt     version, commit, payload checksum, build date
#   <package>/SHA256SUMS.txt     every file above, lowercase, forward slashes
#
# The .deb (optional) installs the same payload system wide under
# /opt/little-tools with /usr/bin/little-tools, a desktop entry, an icon and the
# user guide under /usr/share/doc. It does not touch per user data; see --deb
# notes in docs/USER-GUIDE.md.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$root/.." && pwd)"
app_source="$root/artifacts/linux-x64"
app_binary="$app_source/LittleTools.Assistant"
csproj="$root/LittleTools.Assistant/LittleTools.Assistant.csproj"
user_guide="$root/docs/USER-GUIDE.md"

deb=0
print_version=0
for arg in "$@"; do
  case "$arg" in
    --deb) deb=1 ;;
    --version) print_version=1 ;;
    *) echo "package-linux.sh: unknown option '$arg' (supported: --deb, --version)" >&2; exit 2 ;;
  esac
done

# --- release identity -------------------------------------------------------
# Prefer the identity the payload was actually built with, so the file name can
# never disagree with the binary inside it.
payload_identity=""
if [[ -f "$app_binary" ]]; then
  payload_identity="$(strings -a "$app_binary" 2>/dev/null | grep -m1 -oE '[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?\+[0-9a-f]{40}' 2>/dev/null || true)"
fi
if [[ -z "$payload_identity" && -f "$app_source/LittleTools.Assistant.dll" ]]; then
  # A framework dependent publish keeps the identity in the managed assembly.
  payload_identity="$(strings -a "$app_source/LittleTools.Assistant.dll" 2>/dev/null | grep -m1 -oE '[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?\+[0-9a-f]{40}' 2>/dev/null || true)"
fi
version=""
commit=""
if [[ -n "$payload_identity" ]]; then
  version="${payload_identity%%+*}"
  commit="${payload_identity#*+}"
fi
if [[ -z "$version" ]]; then
  version="$(sed -n 's|.*<Version>\([^<]*\)</Version>.*|\1|p' "$csproj" | head -1)"
fi
[[ -n "$version" ]] || { echo "Cannot determine the release version (no identity in the payload, no <Version> in the csproj)." >&2; exit 1; }

if [[ $print_version -eq 1 ]]; then
  echo "$version"
  exit 0
fi

if [[ ! -x "$app_binary" ]]; then
  echo "Missing publish output: $app_binary" >&2
  echo "Run ./build-linux.sh --publish first." >&2
  exit 1
fi
if [[ -z "$payload_identity" ]]; then
  echo "warning: no build identity found in the payload; using the csproj version $version" >&2
fi

date_stamp="$(date +%Y%m%d)"
package_name="LittleTools-linux-x64-$version-$date_stamp"

# --- staging ----------------------------------------------------------------
stage="$root/.package-stage"
# Keep the staging directory strictly inside CrossPlatform before deleting it.
resolved_stage="$(readlink -f "$stage" 2>/dev/null || echo "$stage")"
if [[ "$(dirname "$resolved_stage")" != "$root" || "$(basename "$resolved_stage")" != ".package-stage" ]]; then
  echo "Package staging directory validation failed." >&2
  exit 1
fi
rm -rf "$resolved_stage"
payload="$resolved_stage/$package_name"
mkdir -p "$payload/App"

cp -a "$app_source/." "$payload/App/"
install -m 755 "$root/install-linux.sh" "$payload/install.sh"
install -m 755 "$root/uninstall-linux.sh" "$payload/uninstall.sh"
install -m 644 "$root/little-tools-assistant.desktop.in" "$payload/"
install -m 644 "$root/README.md" "$payload/DEVELOPER-README.md"
if [[ -f "$user_guide" ]]; then
  install -m 644 "$user_guide" "$payload/README.md"
else
  install -m 644 "$root/README.md" "$payload/README.md"
fi
if [[ -f "$root/HANDOVER.md" ]]; then
  install -m 644 "$root/HANDOVER.md" "$payload/HANDOVER.md"
fi

printf '%s\n' "$version" > "$payload/VERSION"
payload_sha="$(sha256sum "$app_binary" | cut -d' ' -f1)"
{
  echo "Little Tools Linux suite ($package_name)"
  echo "version:       $version"
  echo "commit:        ${commit:-unknown}"
  echo "payload:       App/LittleTools.Assistant"
  echo "payload sha256: $payload_sha"
  echo "packaged:      $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "The running program reports the same version plus a fingerprint of the"
  echo "file it started from, so an installed copy can be identified without"
  echo "trusting the file name:  little-tools --version"
  if [[ -z "$commit" ]]; then
    echo
    echo "NOTE: the payload carried no git commit, so this package does not say"
    echo "which source revision it was built from."
  fi
} > "$payload/BUILD-INFO.txt"

# Checksums, lower case with forward slashes, like the source package manifest.
manifest="$payload/SHA256SUMS.txt"
( cd "$payload" && \
  find . -type f ! -name 'SHA256SUMS.txt' -printf '%P\0' \
  | sort -z \
  | xargs -0 sha256sum \
  | sed 's| \./| |' > "$manifest" )

archive="$repo_root/$package_name.tar.gz"
rm -f "$archive"
tar -czf "$archive" -C "$resolved_stage" "$package_name"

echo "tarball: $archive"
echo "  version: $version${commit:+ (commit ${commit:0:12})}"
echo "  sha256:  $(sha256sum "$archive" | cut -d' ' -f1)"

# --- optional deb -----------------------------------------------------------
if [[ $deb -eq 1 ]]; then
  if ! command -v dpkg-deb >/dev/null 2>&1; then
    echo "dpkg-deb not found; skipping the .deb (the tarball above is complete)." >&2
    rm -rf "$resolved_stage"
    exit 0
  fi

  deb_arch="$(dpkg --print-architecture 2>/dev/null || echo amd64)"
  deb_root="$root/.deb-stage"
  resolved_deb_root="$(readlink -f "$deb_root" 2>/dev/null || echo "$deb_root")"
  if [[ "$(dirname "$resolved_deb_root")" != "$root" || "$(basename "$resolved_deb_root")" != ".deb-stage" ]]; then
    echo "Deb staging directory validation failed." >&2
    exit 1
  fi
  rm -rf "$resolved_deb_root"
  install_root="/opt/little-tools"
  mkdir -p "$resolved_deb_root/DEBIAN" \
           "$resolved_deb_root$install_root" \
           "$resolved_deb_root/usr/bin" \
           "$resolved_deb_root/usr/share/applications" \
           "$resolved_deb_root/usr/share/pixmaps" \
           "$resolved_deb_root/usr/share/doc/little-tools"

  cp -a "$payload/App/." "$resolved_deb_root$install_root/"
  ln -sf "$install_root/LittleTools.Assistant" "$resolved_deb_root/usr/bin/little-tools"
  if [[ -f "$app_source/little-tools.png" ]]; then
    install -m 644 "$app_source/little-tools.png" "$resolved_deb_root/usr/share/pixmaps/little-tools.png"
  fi
  sed \
    -e "s|@EXEC@|/usr/bin/little-tools|g" \
    -e "s|@ICON@|little-tools|g" \
    "$root/little-tools-assistant.desktop.in" > "$resolved_deb_root/usr/share/applications/little-tools.desktop"
  chmod 644 "$resolved_deb_root/usr/share/applications/little-tools.desktop"
  install -m 644 "$payload/README.md" "$resolved_deb_root/usr/share/doc/little-tools/README.md"
  install -m 644 "$payload/BUILD-INFO.txt" "$resolved_deb_root/usr/share/doc/little-tools/BUILD-INFO.txt"
  install -m 644 "$payload/DEVELOPER-README.md" "$resolved_deb_root/usr/share/doc/little-tools/DEVELOPER-README.md"
  if [[ -f "$payload/HANDOVER.md" ]]; then
    install -m 644 "$payload/HANDOVER.md" "$resolved_deb_root/usr/share/doc/little-tools/HANDOVER.md"
  fi
  install -m 644 "$payload/VERSION" "$resolved_deb_root/usr/share/doc/little-tools/VERSION"

  cat > "$resolved_deb_root/usr/share/doc/little-tools/copyright" <<'EOF'
Little Tools (Linux suite host)
-------------------------------

This is a locally built convenience package for the source tree it was built
from; it is not an official Debian archive upload. The upstream tree contains no
LICENSE file, so no license text can be shipped here - treat the package as
"same terms as the source repository".

Source: the Little Tools repository, branch linux-port. The build provenance
(version, git commit, payload checksum) is in BUILD-INFO.txt next to this file.
EOF

  payload_kb="$(du -sk --apparent-size "$resolved_deb_root$install_root" | cut -f1)"
  maintainer="${LITTLE_TOOLS_MAINTAINER:-Little Tools <little-tools@localhost>}"

  cat > "$resolved_deb_root/DEBIAN/control" <<EOF
Package: little-tools
Version: $version
Section: utils
Priority: optional
Architecture: $deb_arch
Maintainer: $maintainer
Installed-Size: $payload_kb
Depends: libc6, libgcc-s1, libstdc++6, libdbus-1-3, libfontconfig1, libfreetype6,
 libexpat1, libx11-6, libxext6, libxrandr2, libxi6, libxcursor1, libxfixes3,
 libice6, libsm6, libgtk-3-0, libglib2.0-0, libgl1, libpng16-16, libbz2-1.0,
 libbrotli1, zlib1g
Recommends: libsecret-tools, maim, gnome-screenshot
Description: Little Tools Linux suite host
 AI assistant (translation, quick ask, screenshot translation), daily todo,
 stock watcher and AI usage monitor in one process, with a tray menu, XDG
 autostart and X11 global hotkeys.
 .
 The per user tarball in the same directory installs into ~/.local/share and
 needs no root; this package installs the same payload to $install_root.
EOF

  cat > "$resolved_deb_root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || true
fi
if [ -x /usr/bin/gtk-update-icon-cache ]; then
  gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor >/dev/null 2>&1 || true
fi
cat <<'MESSAGE'

Little Tools 已安装到 /opt/little-tools（命令：little-tools）。

  little-tools                 启动（托盘 + 快捷键）
  little-tools --toggle        显示/隐藏助手窗口
  little-tools --version       这份拷贝的版本
  little-tools --autostart-enable   按用户开启开机自启（延迟 30 秒）
  little-tools --exit          让正在运行的实例干净退出

如果升级前已经有实例在运行，它仍然跑在旧的文件上：先 little-tools --exit，
再启动，才会真正换到新版本。用户数据（~/.config/little-tools、
~/.local/share/little-tools）不会被安装或卸载删除。
MESSAGE
EOF
  chmod 755 "$resolved_deb_root/DEBIAN/postinst"

  cat > "$resolved_deb_root/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database -q /usr/share/applications >/dev/null 2>&1 || true
fi
# Only removes the directory when dpkg left it empty, so anything a user put
# next to the program (for example a legacy appsettings.json) survives.
rmdir /opt/little-tools 2>/dev/null || true
if [ "$1" = "purge" ]; then
  echo "Little Tools removed. Per user data in ~/.config/little-tools and ~/.local/share/little-tools was kept."
fi
EOF
  chmod 755 "$resolved_deb_root/DEBIAN/postrm"

  deb_file="$repo_root/little-tools_${version}_${deb_arch}.deb"
  rm -f "$deb_file"
  dpkg-deb --build --root-owner-group "$resolved_deb_root" "$deb_file" >/dev/null

  echo "deb:     $deb_file"
  echo "  section: $(dpkg-deb -f "$deb_file" Package) $(dpkg-deb -f "$deb_file" Version) $(dpkg-deb -f "$deb_file" Architecture)"
  echo "  sha256:  $(sha256sum "$deb_file" | cut -d' ' -f1)"
  echo "  verify:  dpkg-deb --info '$deb_file'; dpkg-deb --contents '$deb_file'"
  rm -rf "$resolved_deb_root"
fi

rm -rf "$resolved_stage"
echo "$archive"

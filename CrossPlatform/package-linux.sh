#!/usr/bin/env bash
# Produces the Linux distribution archive from a published build.
#
#   ./package-linux.sh    -> <repo>/LittleTools-linux-x64-YYYYMMDD.tar.gz
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$root/.." && pwd)"
app_source="$root/artifacts/linux-x64"
date_stamp="$(date +%Y%m%d)"
package_name="LittleTools-linux-x64-$date_stamp"
stage="$root/.package-stage"

if [[ ! -x "$app_source/LittleTools.Assistant" ]]; then
  echo "Missing publish output: $app_source/LittleTools.Assistant" >&2
  echo "Run ./build-linux.sh --publish first." >&2
  exit 1
fi

# Keep the staging directory strictly inside CrossPlatform before deleting it.
resolved_stage="$(readlink -f "$stage" 2>/dev/null || echo "$stage")"
if [[ "$(dirname "$resolved_stage")" != "$root" || "$(basename "$resolved_stage")" != ".package-stage" ]]; then
  echo "Package staging directory validation failed." >&2
  exit 1
fi
rm -rf "$resolved_stage"
mkdir -p "$resolved_stage/$package_name/App"

cp -a "$app_source/." "$resolved_stage/$package_name/App/"
install -m 755 "$root/install-linux.sh" "$resolved_stage/$package_name/install.sh"
install -m 755 "$root/uninstall-linux.sh" "$resolved_stage/$package_name/uninstall.sh"
install -m 644 "$root/little-tools-assistant.desktop.in" "$resolved_stage/$package_name/"
install -m 644 "$root/README.md" "$resolved_stage/$package_name/"

# Checksums, lower case with forward slashes, like the source package manifest.
manifest="$resolved_stage/$package_name/SHA256SUMS.txt"
( cd "$resolved_stage/$package_name" && \
  find . -type f ! -name 'SHA256SUMS.txt' -printf '%P\0' \
  | sort -z \
  | xargs -0 sha256sum \
  | sed 's| \./| |' > "$manifest" )

archive="$repo_root/$package_name.tar.gz"
rm -f "$archive"
tar -czf "$archive" -C "$resolved_stage" "$package_name"
rm -rf "$resolved_stage"
echo "$archive"

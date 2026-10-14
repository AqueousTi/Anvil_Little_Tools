#!/usr/bin/env bash
# .deb lifecycle check without touching the system: the package is installed,
# upgraded and purged inside a throwaway dpkg root with
# `dpkg --root=... --force-not-root --force-script-chrootless`, the payload is
# then run from that root with a throwaway HOME, and the per user data next to it
# is compared byte for byte.
#
#   deb-verify.sh [--package FILE] [OUTDIR]
#
# The real dpkg database is only ever queried (to prove little-tools is not
# installed system wide); nothing is written outside OUTDIR and /tmp.
#
# The upgrade is exercised by rebuilding the same payload as a fake 0.9.0 package
# with a rewritten control version (zstd, because that package only exists for
# this test), so dpkg really does replace an older installation.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$ROOT/CrossPlatform/tools/env.sh"

package=""
outdir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --package) package="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,15p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) outdir="$1"; shift ;;
  esac
done
if [[ -z "$package" ]]; then
  # shellcheck disable=SC2012
  package="$(ls -t "$ROOT"/little-tools_*.deb 2>/dev/null | head -1)"
fi
if [[ ! -f "$package" ]]; then
  echo "deb-verify: no .deb found (pass --package FILE)" >&2
  exit 2
fi
if ! command -v dpkg-deb >/dev/null 2>&1; then
  echo "deb-verify: dpkg-deb is required" >&2
  exit 2
fi
outdir="${outdir:-$ROOT/.tools/out/deb-verify}"
root="/tmp/lt-deb-verify-root"
tmpd="/tmp/lt-deb-verify-tmp"
old_tree="$outdir/old-0.9.0"
old_deb="$outdir/little-tools_0.9.0_amd64.deb"
sandbox="$outdir/home"
log="$outdir/run.log"

pass=0
fail=0
check() {
  if [[ "$2" -eq 0 ]]; then
    echo "  PASS  $1"
    pass=$((pass + 1))
  else
    echo "  FAIL  $1"
    fail=$((fail + 1))
  fi
}
step() { echo; echo "== $*"; }

dpkgroot() {
  dpkg --root="$root" --admindir="$root/var/lib/dpkg" --log="$outdir/dpkg.log" \
       --force-not-root --force-script-chrootless --force-bad-path --force-depends "$@"
}
query() { dpkg-query --admindir="$root/var/lib/dpkg" -W -f="$1" little-tools 2>/dev/null; }

rm -rf "$outdir" "$root" "$tmpd" "$sandbox"
mkdir -p "$outdir" "$tmpd" "$sandbox" "$root/var/lib/dpkg/info" "$root/var/lib/dpkg/updates" \
         "$root/var/lib/dpkg/triggers" "$root/var/log" "$root/opt" "$root/usr"
: > "$root/var/lib/dpkg/status"
exec > >(tee "$log") 2>&1

echo "package: $(readlink -f "$package")"
echo "fake dpkg root: $root (the real system is only queried)"

step "package metadata"
version="$(dpkg-deb -f "$package" Version)"
arch="$(dpkg-deb -f "$package" Architecture)"
name="$(dpkg-deb -f "$package" Package)"
echo "  $name $version $arch"
check "control parses (Package/Version/Architecture)" \
  "$([[ "$name" == "little-tools" && -n "$version" && -n "$arch" ]] && echo 0 || echo 1)"
check "file name matches the control version" \
  "$([[ "$(basename "$package")" == "little-tools_${version}_${arch}.deb" ]] && echo 0 || echo 1)"
for script in postinst postrm; do
  dpkg-deb --control "$package" "$outdir/control" 2>/dev/null
  sh -n "$outdir/control/$script" 2>/dev/null
  check "$script is valid /bin/sh" $?
done
dpkg-deb --contents "$package" > "$outdir/contents.txt" 2>/dev/null
check "ships the payload in /opt/little-tools" \
  "$(grep -q '^.*\./opt/little-tools/LittleTools.Assistant$' "$outdir/contents.txt" && echo 0 || echo 1)"
check "ships the /usr/bin/little-tools symlink" \
  "$(grep -q ' \./usr/bin/little-tools -> /opt/little-tools/LittleTools.Assistant$' "$outdir/contents.txt" && echo 0 || echo 1)"
check "ships a desktop entry and an icon" \
  "$(grep -q '\./usr/share/applications/little-tools.desktop$' "$outdir/contents.txt" \
     && grep -q '\./usr/share/pixmaps/little-tools.png$' "$outdir/contents.txt" && echo 0 || echo 1)"
check "ships the user guide, developer README, handover and BUILD-INFO" \
  "$(grep -q '\./usr/share/doc/little-tools/README.md$' "$outdir/contents.txt" \
     && grep -q '\./usr/share/doc/little-tools/DEVELOPER-README.md$' "$outdir/contents.txt" \
     && grep -q '\./usr/share/doc/little-tools/HANDOVER.md$' "$outdir/contents.txt" \
     && grep -q '\./usr/share/doc/little-tools/BUILD-INFO.txt$' "$outdir/contents.txt" && echo 0 || echo 1)"

step "fake 0.9.0 package with the same payload, for the upgrade test"
mkdir -p "$old_tree"
dpkg-deb --extract "$package" "$old_tree"
dpkg-deb --control "$package" "$old_tree/DEBIAN"
sed -i "s/^Version: .*/Version: 0.9.0/" "$old_tree/DEBIAN/control"
dpkg-deb --build --root-owner-group -Zzstd "$old_tree" "$old_deb" > /dev/null
check "fake 0.9.0 package builds" $?
check "fake package reports version 0.9.0" \
  "$([[ "$(dpkg-deb -f "$old_deb" Version)" == "0.9.0" ]] && echo 0 || echo 1)"

step "install 0.9.0, then upgrade to $version"
mkdir -p "$sandbox/.config/little-tools" "$sandbox/.local/share/little-tools/todo"
printf '{"AssistantEnabled":true,"TodoEnabled":false}\n' > "$sandbox/.config/little-tools/manager.json"
printf '{"Version":1,"Days":[]}\n' > "$sandbox/.local/share/little-tools/todo/data.json"
( cd "$sandbox" && find .config -type f | sort | xargs md5sum ) > "$outdir/seed.md5"
dpkgroot -i "$old_deb" > "$outdir/install-old.txt" 2>&1
check "dpkg --root installs the fake 0.9.0" $?
check "dpkg database reports 0.9.0 installed" \
  "$([[ "$(query '${Version}')" == "0.9.0" ]] && echo 0 || echo 1)"
dpkgroot -i "$package" > "$outdir/upgrade.txt" 2>&1
check "dpkg --root upgrades to $version" $?
check "dpkg database reports $version installed" \
  "$([[ "$(query '${Version}')" == "$version" ]] && echo 0 || echo 1)"
check "package state is install ok installed" \
  "$(dpkg --root="$root" --admindir="$root/var/lib/dpkg" -s little-tools 2>/dev/null | grep -q 'Status: install ok installed' && echo 0 || echo 1)"
check "postinst banner ran during the upgrade" \
  "$(grep -q '已安装到 /opt/little-tools' "$outdir/upgrade.txt" && echo 0 || echo 1)"
check "no .dpkg-new/.dpkg-old leftovers" \
  "$(find "$root/opt" "$root/usr" -name '*.dpkg-*' 2>/dev/null | grep -q . && echo 1 || echo 0)"
check "payload binary is executable" \
  "$([[ -x "$root/opt/little-tools/LittleTools.Assistant" ]] && echo 0 || echo 1)"

step "run the dpkg-installed payload in a throwaway HOME"
export HOME="$sandbox" TMPDIR="$tmpd" DISPLAY="${DISPLAY:-:1}"
unset XDG_CONFIG_HOME XDG_DATA_HOME XDG_CACHE_HOME
export XMODIFIERS=@im=none
got="$("$root/opt/little-tools/LittleTools.Assistant" --version 2>/dev/null)"
check "--version exits 0 on the installed payload" $?
check "--version reports the packaged version ($got)" \
  "$([[ "$got" == "$version"* ]] && echo 0 || echo 1)"
rm -f "$outdir/diagnose.json"
"$root/opt/little-tools/LittleTools.Assistant" --diagnose "$outdir/diagnose.json" > /dev/null 2>&1
check "--diagnose exits 0" $?
check "--diagnose wrote a JSON object with a session" \
  "$(python3 -c "import json,sys;d=json.load(open('$outdir/diagnose.json'));sys.exit(0 if d.get('session') else 1)" 2>/dev/null && echo 0 || echo 1)"
( cd "$sandbox" && md5sum -c "$outdir/seed.md5" > /dev/null 2>&1 )
check "seeded user config unchanged by install and upgrade" $?
check "todo data file kept" $([[ -f "$sandbox/.local/share/little-tools/todo/data.json" ]] && echo 0 || echo 1)

step "purge the package from the fake root"
dpkgroot --purge little-tools > "$outdir/purge.txt" 2>&1
check "dpkg --root purge exits 0" $?
check "dpkg database no longer knows little-tools" "$(query '${Status}' | grep -q . && echo 1 || echo 0)"
check "program files removed" \
  "$(find "$root/opt" "$root/usr" -name 'LittleTools.Assistant' -o -name 'little-tools.desktop' 2>/dev/null | grep -q . && echo 1 || echo 0)"
check "postrm kept per user data (it never touches \$HOME)" \
  "$([[ -f "$sandbox/.local/share/little-tools/todo/data.json" && -f "$sandbox/.config/little-tools/manager.json" ]] && echo 0 || echo 1)"

step "the real system was not modified"
check "little-tools is not installed in the real dpkg database" \
  "$(dpkg -s little-tools > /dev/null 2>&1 && echo 1 || echo 0)"

echo
echo "deb-verify: pass=$pass fail=$fail"
echo "log: $log"
[[ $fail -eq 0 ]]

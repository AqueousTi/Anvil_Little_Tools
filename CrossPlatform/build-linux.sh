#!/usr/bin/env bash
# Builds, tests and publishes the Little Tools Linux suite host.
#
#   ./build-linux.sh              restore, build, run the protocol tests
#   ./build-linux.sh --publish    also produce artifacts/linux-x64
#
# A repository local SDK at <repo>/.tools/dotnet is preferred, matching the
# Windows build scripts, so the pinned global.json version is used.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$root/.." && pwd)"
local_dotnet="$repo_root/.tools/dotnet/dotnet"
dotnet="$local_dotnet"
[[ -x "$dotnet" ]] || dotnet="dotnet"

solution="$root/LittleTools.CrossPlatform.slnx"
tests="$root/LittleTools.Assistant.Tests/LittleTools.Assistant.Tests.csproj"
project="$root/LittleTools.Assistant/LittleTools.Assistant.csproj"

publish=0
[[ "${1:-}" == "--publish" ]] && publish=1

echo "== restore =="
"$dotnet" restore "$solution"

echo "== build =="
"$dotnet" build "$solution" -c Release --no-restore

echo "== tests =="
"$dotnet" run --project "$tests" -c Release --no-build

if [[ $publish -eq 1 ]]; then
  destination="$root/artifacts/linux-x64"
  echo "== publish linux-x64 =="
  # A plain publish can treat the RID specific intermediate as up to date and
  # ship a stale binary, so the RID build is forced first.
  "$dotnet" build "$project" -c Release -r linux-x64 --self-contained true --no-incremental
  "$dotnet" publish "$project" -c Release -r linux-x64 --self-contained true --no-build -o "$destination"
  find "$destination" -name '*.pdb' -delete
  echo "$destination"
fi

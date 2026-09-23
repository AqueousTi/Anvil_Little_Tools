# Local Linux build environment for this workspace.
# The DSH file sandbox allows writes only under the workspace, so every
# SDK/NuGet/CLI path is redirected here instead of $HOME. .tools/ is gitignored.
export WORKSPACE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export DOTNET_ROOT="$WORKSPACE_ROOT/.tools/dotnet"
export DOTNET_CLI_HOME="$WORKSPACE_ROOT/.tools/home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES="$WORKSPACE_ROOT/.tools/nuget"
# Avalonia's build task writes a telemetry log under $HOME/.local/share.
# The sandbox denies that, so opt out instead of failing the build.
export AVALONIA_TELEMETRY_OPTOUT=1
export PATH="$DOTNET_ROOT:$PATH"
mkdir -p "$DOTNET_CLI_HOME" "$NUGET_PACKAGES"

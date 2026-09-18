#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS_DIR="${MAIL_ASSISTANT_TOOLS_DIR:-$ROOT_DIR/.local-tools}"
DOTNET_DIR="$TOOLS_DIR/dotnet"
DOTNET_BIN="$DOTNET_DIR/dotnet"
APP_ONLY=0

if [[ "${1:-}" == "--app-only" ]]; then
  APP_ONLY=1
  shift
fi

if (( $# > 0 )); then
  printf '%s\n' "用法：$0 [--app-only]" >&2
  exit 2
fi

"$ROOT_DIR/script/bootstrap-local-build-tools.sh"

export DOTNET_ROOT="$DOTNET_DIR"
export DOTNET_CLI_HOME="$TOOLS_DIR/dotnet-home"
export NUGET_PACKAGES="$TOOLS_DIR/nuget-packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

printf '%s\n' "正在还原依赖并构建 Release..."
"$DOTNET_BIN" restore "$ROOT_DIR/MailArchiver.csproj" \
  --packages "$NUGET_PACKAGES" \
  --ignore-failed-sources \
  -p:NuGetAudit=false
"$DOTNET_BIN" build "$ROOT_DIR/MailArchiver.csproj" \
  --configuration Release \
  --no-restore \
  --maxcpucount:1 \
  --nodeReuse:false \
  -p:BuildInParallel=false \
  -p:UseSharedCompilation=false \
  -p:NuGetAudit=false

runtime_path="$($DOTNET_BIN --list-runtimes | awk '/^Microsoft.NETCore.App/ {gsub(/[\[\]]/, "", $NF); print $NF; exit}')"
if [[ -z "$runtime_path" ]]; then
  printf '%s\n' "本地 SDK 中未找到 Microsoft.NETCore.App 运行时。" >&2
  exit 3
fi
runtime_root="$(dirname "$(dirname "$runtime_path")")"

if (( APP_ONLY )); then
  DOTNET_BIN="$DOTNET_BIN" \
  DOTNET_RUNTIME_ROOT="$runtime_root" \
  NUGET_PACKAGES="$NUGET_PACKAGES" \
    "$ROOT_DIR/local-app/build-dmg.sh" --app-only
else
  DOTNET_BIN="$DOTNET_BIN" \
  DOTNET_RUNTIME_ROOT="$runtime_root" \
  NUGET_PACKAGES="$NUGET_PACKAGES" \
    "$ROOT_DIR/local-app/build-dmg.sh"
fi

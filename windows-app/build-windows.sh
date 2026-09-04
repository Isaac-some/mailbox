#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT_DIR="${KOUZI_WINDOWS_OUTPUT_DIR:-$SCRIPT_DIR/build/邮箱助手-Windows-x64}"
DOTNET_COMMAND="${DOTNET_BIN:-dotnet}"
WORK_DIR="$(mktemp -d "${TMPDIR:-/private/tmp}/kouzi-windows-build.XXXXXX")"
STAGING_DIR="$WORK_DIR/邮箱助手-Windows-x64"
SERVER_DIR="$STAGING_DIR/server"

cleanup() {
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

mkdir -p "$SERVER_DIR"

"$DOTNET_COMMAND" publish "$PROJECT_DIR/MailArchiver.csproj" \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  --output "$SERVER_DIR" \
  --maxcpucount:1 \
  --nodeReuse:false \
  -p:BuildInParallel=false \
  -p:UseSharedCompilation=false \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false

# Keep static assets beside the server binaries. The generated static-web-assets
# manifest may otherwise reference the source checkout instead of this package.
if [[ -d "$PROJECT_DIR/wwwroot" ]]; then
  ditto "$PROJECT_DIR/wwwroot" "$SERVER_DIR/wwwroot"
fi

"$DOTNET_COMMAND" publish "$SCRIPT_DIR/KouziMailAssistant.Windows.csproj" \
  --configuration Release \
  --runtime win-x64 \
  --self-contained true \
  --output "$STAGING_DIR" \
  --maxcpucount:1 \
  --nodeReuse:false \
  -p:BuildInParallel=false \
  -p:UseSharedCompilation=false \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishTrimmed=false

for required_file in \
  "$STAGING_DIR/KouziMailAssistant.exe" \
  "$SERVER_DIR/MailArchiver.exe" \
  "$SERVER_DIR/appsettings.Local.json"; do
  if [[ ! -f "$required_file" ]]; then
    printf '%s\n' "打包失败，缺少文件：$required_file" >&2
    exit 4
  fi
done

mv "$STAGING_DIR/KouziMailAssistant.exe" "$STAGING_DIR/邮箱助手.exe"
cp "$SCRIPT_DIR/README.md" "$STAGING_DIR/README.md"

rm -rf "$OUTPUT_DIR"
mkdir -p "$(dirname "$OUTPUT_DIR")"
mv "$STAGING_DIR" "$OUTPUT_DIR"

printf '%s\n' "$OUTPUT_DIR"

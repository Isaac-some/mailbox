#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_BIN="$ROOT_DIR/.local-tools/dotnet/dotnet"
BUILD_DIR="$ROOT_DIR/windows-app/build"
APP_VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT_DIR/windows-app/MailAssistant.Windows.csproj" | head -n 1)"
PACKAGE_NAME="MailAssistant-Windows-x64-v$APP_VERSION"

if [[ -z "${MAIL_ASSISTANT_UPSTREAM_TOKEN:-}" ]]; then
  printf '%s\n' "缺少租户 Token，不能生成 Windows 交付包。" >&2
  exit 2
fi
if [[ ! -x "$DOTNET_BIN" ]]; then
  printf '%s\n' "缺少本地 .NET SDK，请先运行 ./script/bootstrap-local-build-tools.sh。" >&2
  exit 2
fi
if [[ ! -f "$ROOT_DIR/appsettings.Local.template.json" ]]; then
  printf '%s\n' "缺少本地版配置模板。" >&2
  exit 2
fi
if [[ -e "$BUILD_DIR/$PACKAGE_NAME" || -e "$BUILD_DIR/MailAssistant-Windows-x64-v$APP_VERSION.zip" ]]; then
  printf '%s\n' "同版本 Windows 交付包已存在，请先确认现有交付物。" >&2
  exit 2
fi

export DOTNET_ROOT="$ROOT_DIR/.local-tools/dotnet"
export DOTNET_CLI_HOME="$ROOT_DIR/.local-tools/dotnet-home"
export NUGET_PACKAGES="$ROOT_DIR/.local-tools/nuget-packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

mkdir -p "$BUILD_DIR"
STAGING_DIR="$(mktemp -d "$BUILD_DIR/.windows-staging.XXXXXX")"
trap 'rm -rf "$STAGING_DIR"' EXIT
OUTPUT_DIR="$STAGING_DIR/$PACKAGE_NAME"
SERVER_DIR="$OUTPUT_DIR/server"
mkdir -p "$SERVER_DIR"

"$DOTNET_BIN" restore "$ROOT_DIR/MailArchiver.csproj" --runtime win-x64 --packages "$NUGET_PACKAGES" -p:NuGetAudit=false
"$DOTNET_BIN" publish "$ROOT_DIR/MailArchiver.csproj" \
  --configuration Release --runtime win-x64 --self-contained true --no-restore \
  --output "$SERVER_DIR" -p:PublishSingleFile=false -p:PublishTrimmed=false -p:WarningLevel=0

"$DOTNET_BIN" restore "$ROOT_DIR/windows-app/MailAssistant.Windows.csproj" --runtime win-x64 --packages "$NUGET_PACKAGES" -p:NuGetAudit=false
"$DOTNET_BIN" publish "$ROOT_DIR/windows-app/MailAssistant.Windows.csproj" \
  --configuration Release --runtime win-x64 --self-contained true --no-restore \
  --output "$OUTPUT_DIR" -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:WarningLevel=0

if [[ ! -d "$SERVER_DIR/wwwroot" ]]; then
  cp -R "$ROOT_DIR/wwwroot" "$SERVER_DIR/wwwroot"
fi
cp "$ROOT_DIR/appsettings.Local.template.json" "$SERVER_DIR/appsettings.Local.json"
if [[ -n "${MAIL_ASSISTANT_UPSTREAM_ENDPOINT:-}" ]]; then
  jq --arg endpoint "$MAIL_ASSISTANT_UPSTREAM_ENDPOINT" \
    '.UpstreamMailboxSync.BearerToken = env.MAIL_ASSISTANT_UPSTREAM_TOKEN | .UpstreamMailboxSync.Endpoint = $endpoint' \
    "$SERVER_DIR/appsettings.Local.json" > "$SERVER_DIR/appsettings.Local.json.tmp"
else
  jq '.UpstreamMailboxSync.BearerToken = env.MAIL_ASSISTANT_UPSTREAM_TOKEN' \
    "$SERVER_DIR/appsettings.Local.json" > "$SERVER_DIR/appsettings.Local.json.tmp"
fi
mv "$SERVER_DIR/appsettings.Local.json.tmp" "$SERVER_DIR/appsettings.Local.json"
mv "$OUTPUT_DIR/MailAssistant.exe" "$OUTPUT_DIR/邮箱助手.exe"
cp "$ROOT_DIR/windows-app/README.md" "$OUTPUT_DIR/README.md"
test -f "$SERVER_DIR/MailArchiver.exe"
test -f "$OUTPUT_DIR/邮箱助手.exe"

(cd "$STAGING_DIR" && zip -q -r -X "MailAssistant-Windows-x64-v$APP_VERSION.zip" "$PACKAGE_NAME")
mv "$OUTPUT_DIR" "$BUILD_DIR/$PACKAGE_NAME"
mv "$STAGING_DIR/MailAssistant-Windows-x64-v$APP_VERSION.zip" "$BUILD_DIR/"
printf '%s\n' "$BUILD_DIR/MailAssistant-Windows-x64-v$APP_VERSION.zip"

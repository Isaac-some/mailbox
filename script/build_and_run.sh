#!/usr/bin/env bash
set -euo pipefail

MODE="${1:-run}"
APP_NAME="邮箱助手"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN_ROOT="${KOUZI_RUN_ROOT:-/private/tmp/kouzi-mail-assistant-run}"
PUBLISH_DIR="$RUN_ROOT/server"
APP_OUTPUT_DIR="$RUN_ROOT/app"
APP_BUNDLE="$APP_OUTPUT_DIR/$APP_NAME.app"

resolve_dotnet() {
  if [[ -n "${DOTNET_BIN:-}" ]]; then
    printf '%s\n' "$DOTNET_BIN"
  elif command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
  elif [[ -x /private/tmp/kouzi-dotnet-sdk/dotnet ]]; then
    printf '%s\n' /private/tmp/kouzi-dotnet-sdk/dotnet
  else
    printf '%s\n' "未找到 .NET 10 SDK。请先安装 .NET 10，或设置 DOTNET_BIN。" >&2
    exit 3
  fi
}

DOTNET_COMMAND="$(resolve_dotnet)"
RUNTIME_PATH="$($DOTNET_COMMAND --list-runtimes | awk '/^Microsoft.NETCore.App/ {gsub(/[\[\]]/, "", $NF); print $NF; exit}')"
if [[ -z "$RUNTIME_PATH" ]]; then
  printf '%s\n' "未找到 Microsoft.NETCore.App 运行时。" >&2
  exit 3
fi
DOTNET_RUNTIME_ROOT="$(dirname "$(dirname "$RUNTIME_PATH")")"

case "$MODE" in
  run|--debug|debug|--logs|logs|--telemetry|telemetry|--verify|verify)
    ;;
  *)
    printf '%s\n' "用法：$0 [run|--debug|--logs|--telemetry|--verify]" >&2
    exit 2
    ;;
esac

stop_running_app() {
  local temporary_server_assembly="$APP_BUNDLE/Contents/Resources/server/MailArchiver.dll"
  local packaged_server_assembly="$ROOT_DIR/local-app/build/$APP_NAME.app/Contents/Resources/server/MailArchiver.dll"
  local pid command

  pkill -x "$APP_NAME" >/dev/null 2>&1 || true

  # The native wrapper may already be gone while its embedded .NET server is
  # still listening. Stop only servers belonging to this repository's test or
  # packaged app bundles so a stale build cannot keep port 5180 occupied.
  while read -r pid command; do
    if [[ "$command" == *"$temporary_server_assembly"* ||
          "$command" == *"$packaged_server_assembly"* ]]; then
      kill "$pid" >/dev/null 2>&1 || true
    fi
  done < <(ps -ax -o pid=,command=)

  sleep 1
}

stop_running_app
mkdir -p "$RUN_ROOT"
rm -rf "$PUBLISH_DIR" "$APP_OUTPUT_DIR"

"$DOTNET_COMMAND" publish "$ROOT_DIR/MailArchiver.csproj" \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained false \
  --output "$PUBLISH_DIR" \
  --maxcpucount:1 \
  --nodeReuse:false \
  -p:BuildInParallel=false \
  -p:UseSharedCompilation=false \
  -p:PublishTrimmed=false

DOTNET_BIN="$DOTNET_COMMAND" \
DOTNET_RUNTIME_ROOT="$DOTNET_RUNTIME_ROOT" \
SERVER_PUBLISH_DIR="$PUBLISH_DIR" \
KOUZI_APP_OUTPUT_DIR="$APP_OUTPUT_DIR" \
  "$ROOT_DIR/local-app/build-dmg.sh" --app-only >/dev/null

open_app() {
  /usr/bin/open -n "$APP_BUNDLE"
}

case "$MODE" in
  run)
    open_app
    ;;
  --debug|debug)
    lldb -- "$APP_BUNDLE/Contents/MacOS/$APP_NAME"
    ;;
  --logs|logs)
    open_app
    /usr/bin/log stream --info --style compact --predicate "process == \"$APP_NAME\""
    ;;
  --telemetry|telemetry)
    open_app
    /usr/bin/log stream --info --style compact --predicate 'subsystem == "com.kouzi.mailassistant"'
    ;;
  --verify|verify)
    open_app
    sleep 3
    pgrep -x "$APP_NAME" >/dev/null
    printf '%s\n' "$APP_BUNDLE"
    ;;
esac

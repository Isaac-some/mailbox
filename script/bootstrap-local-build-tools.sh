#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TOOLS_DIR="${MAIL_ASSISTANT_TOOLS_DIR:-$ROOT_DIR/.local-tools}"
DOTNET_DIR="$TOOLS_DIR/dotnet"
CACHE_DIR="$TOOLS_DIR/cache"
NUGET_PACKAGES_DIR="$TOOLS_DIR/nuget-packages"
DOTNET_SDK_VERSION="${DOTNET_SDK_VERSION:-10.0.401}"
DOTNET_BIN="$DOTNET_DIR/dotnet"
INSTALL_SCRIPT="$CACHE_DIR/dotnet-install.sh"

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  printf '%s\n' "本地 macOS 打包目前只支持 Apple Silicon。" >&2
  exit 2
fi

mkdir -p "$DOTNET_DIR" "$CACHE_DIR" "$TOOLS_DIR/dotnet-home" "$NUGET_PACKAGES_DIR"

has_required_sdk() {
  [[ -x "$DOTNET_BIN" ]] && "$DOTNET_BIN" --list-sdks 2>/dev/null | awk '{print $1}' | grep -Fxq "$DOTNET_SDK_VERSION"
}

if ! has_required_sdk; then
  temporary_installer="$(mktemp "$CACHE_DIR/dotnet-install.XXXXXX")"
  trap 'rm -f "$temporary_installer"' EXIT

  printf '%s\n' "正在下载 Microsoft 官方 .NET 安装脚本..."
  curl --fail --location --silent --show-error \
    https://dot.net/v1/dotnet-install.sh \
    --output "$temporary_installer"
  chmod +x "$temporary_installer"
  mv "$temporary_installer" "$INSTALL_SCRIPT"
  trap - EXIT

  printf '%s\n' "正在安装 .NET SDK $DOTNET_SDK_VERSION 到 $DOTNET_DIR"
  "$INSTALL_SCRIPT" \
    --version "$DOTNET_SDK_VERSION" \
    --architecture arm64 \
    --os osx \
    --install-dir "$DOTNET_DIR" \
    --no-path
fi

if ! has_required_sdk; then
  printf '%s\n' "本地 .NET SDK 安装不完整：$DOTNET_SDK_VERSION" >&2
  exit 3
fi

if [[ -z "$(find "$NUGET_PACKAGES_DIR" -mindepth 1 -maxdepth 1 -print -quit)" &&
      -d "$HOME/.nuget/packages" ]]; then
  printf '%s\n' "正在从本机现有 NuGet 缓存初始化工作区缓存..."
  /bin/cp -cR "$HOME/.nuget/packages/." "$NUGET_PACKAGES_DIR/"
fi

printf '%s\n' "本地构建工具已就绪：$DOTNET_BIN"
printf '%s\n' "SDK：$($DOTNET_BIN --version)"

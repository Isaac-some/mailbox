#!/bin/zsh
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="${KOUZI_BUILD_DIR:-$SCRIPT_DIR/build}"
APP_NAME="邮箱助手"
DMG_PATH="$BUILD_DIR/$APP_NAME-AppleSilicon.dmg"
ICON_FILE="$SCRIPT_DIR/AppIcon.icns"
DOTNET_BIN="${DOTNET_BIN:-dotnet}"
DOTNET_RUNTIME_ROOT="${DOTNET_RUNTIME_ROOT:-}"
DOTNET_BROTLI_LIB_DIR="${DOTNET_BROTLI_LIB_DIR:-}"
SERVER_PUBLISH_DIR="${SERVER_PUBLISH_DIR:-}"
APP_ONLY=0

if [[ "${1:-}" == "--app-only" ]]; then
  APP_ONLY=1
  shift
fi

if (( $# > 0 )); then
  print -u2 "用法：$0 [--app-only]"
  exit 2
fi

if [[ ! -f "$ICON_FILE" ]]; then
  print -u2 "缺少 App 图标文件：$ICON_FILE"
  exit 2
fi

if [[ -z "$DOTNET_RUNTIME_ROOT" ]]; then
  RUNTIME_PATH="$("$DOTNET_BIN" --list-runtimes | awk '/^Microsoft.NETCore.App/ {gsub(/[\[\]]/, "", $NF); print $NF; exit}')"
  if [[ -z "$RUNTIME_PATH" ]]; then
    print -u2 "无法定位 .NET 运行时目录。请设置 DOTNET_RUNTIME_ROOT。"
    exit 3
  fi
  DOTNET_RUNTIME_ROOT="$(dirname "$(dirname "$RUNTIME_PATH")")"
fi

for runtime_item in dotnet host shared; do
  if [[ ! -e "$DOTNET_RUNTIME_ROOT/$runtime_item" ]]; then
    print -u2 "缺少运行时文件：$DOTNET_RUNTIME_ROOT/$runtime_item"
    exit 3
  fi
done

TEMP_ROOT="${TMPDIR:-/private/tmp/}"
WORK_DIR="$(mktemp -d "${TEMP_ROOT%/}/kouzi-mail-assistant.XXXXXX")"
APP_BUILD_PATH="$WORK_DIR/$APP_NAME.app"
if (( APP_ONLY )); then
  APP_OUTPUT_DIR="${KOUZI_APP_OUTPUT_DIR:-/private/tmp/kouzi-mail-assistant-test}"
else
  APP_OUTPUT_DIR="$BUILD_DIR"
fi
APP_PATH="$APP_OUTPUT_DIR/$APP_NAME.app"
trap 'rm -rf "$WORK_DIR"' EXIT

if (( APP_ONLY )); then
  rm -rf "$BUILD_DIR/swift-module-cache" "$BUILD_DIR/AppIcon.iconset"
else
  rm -rf "$BUILD_DIR"
fi
mkdir -p "$BUILD_DIR"
mkdir -p "$APP_BUILD_PATH/Contents/MacOS" "$APP_BUILD_PATH/Contents/Resources/server" "$APP_BUILD_PATH/Contents/Resources/dotnet"

if [[ -n "$SERVER_PUBLISH_DIR" ]]; then
  if [[ ! -f "$SERVER_PUBLISH_DIR/MailArchiver.dll" ]]; then
    print -u2 "预发布目录缺少 MailArchiver.dll：$SERVER_PUBLISH_DIR"
    exit 4
  fi
  ditto "$SERVER_PUBLISH_DIR" "$APP_BUILD_PATH/Contents/Resources/server"
else
  "$DOTNET_BIN" publish "$PROJECT_DIR/MailArchiver.csproj" \
    --configuration Release \
    --no-build \
    --output "$APP_BUILD_PATH/Contents/Resources/server" \
    -p:CompressionEnabled=false \
    -p:BuildInParallel=false \
    -p:UseSharedCompilation=false \
    -p:PublishTrimmed=false
fi

for excluded_directory in local-app tests; do
  if [[ -e "$APP_BUILD_PATH/Contents/Resources/server/$excluded_directory" ]]; then
    print -u2 "服务发布物错误包含目录：$excluded_directory"
    exit 4
  fi
done

cp "$DOTNET_RUNTIME_ROOT/dotnet" "$APP_BUILD_PATH/Contents/Resources/dotnet/dotnet"
ditto "$DOTNET_RUNTIME_ROOT/host" "$APP_BUILD_PATH/Contents/Resources/dotnet/host"
ditto "$DOTNET_RUNTIME_ROOT/shared" "$APP_BUILD_PATH/Contents/Resources/dotnet/shared"

COMPRESSION_NATIVE="$(find \
  "$APP_BUILD_PATH/Contents/Resources/dotnet/shared/Microsoft.NETCore.App" \
  -mindepth 2 \
  -maxdepth 2 \
  -name libSystem.IO.Compression.Native.dylib \
  -print \
  -quit)"
if [[ -z "$COMPRESSION_NATIVE" ]]; then
  print -u2 "内嵌运行时缺少 libSystem.IO.Compression.Native.dylib。"
  exit 3
fi

BROTLI_DEPENDENCY="$(otool -L "$COMPRESSION_NATIVE" | awk '$1 ~ /libbrotli(dec|enc)/ {print $1; exit}')"
if [[ -n "$BROTLI_DEPENDENCY" && -z "$DOTNET_BROTLI_LIB_DIR" ]]; then
  print -u2 "当前 .NET 运行时依赖外部 Brotli；请设置 DOTNET_BROTLI_LIB_DIR 后再构建。"
  exit 3
fi

if [[ -n "$DOTNET_BROTLI_LIB_DIR" ]]; then
  modified_libraries=()
  ditto "$DOTNET_BROTLI_LIB_DIR" "$APP_BUILD_PATH/Contents/Resources/dotnet/brotli/lib"
  modified_libraries+=("$COMPRESSION_NATIVE")

  for library in libbrotlidec.1.dylib libbrotlienc.1.dylib; do
    old_path="$(otool -L "$COMPRESSION_NATIVE" | awk -v library="$library" '$1 ~ library {print $1; exit}')"
    if [[ -n "$old_path" ]]; then
      install_name_tool -change "$old_path" "@loader_path/../../../brotli/lib/$library" "$COMPRESSION_NATIVE"
    fi
  done

  for library_base in libbrotlidec libbrotlienc; do
    bundled_library="$(find \
      "$APP_BUILD_PATH/Contents/Resources/dotnet/brotli/lib" \
      -maxdepth 1 \
      -type f \
      -name "$library_base.*.dylib" \
      -print \
      -quit)"
    if [[ -z "$bundled_library" ]]; then
      print -u2 "内嵌 Brotli 目录缺少 $library_base 动态库。"
      exit 3
    fi

    old_common_path="$(otool -L "$bundled_library" | awk '$1 ~ /libbrotlicommon\.[0-9]+\.dylib$/ {print $1; exit}')"
    if [[ -n "$old_common_path" ]]; then
      install_name_tool -change "$old_common_path" "@loader_path/${old_common_path:t}" "$bundled_library"
    fi
    modified_libraries+=("$bundled_library")
  done
fi

xcrun swiftc -O \
  -parse-as-library \
  -target arm64-apple-macos15.0 \
  -module-cache-path "$WORK_DIR/swift-module-cache" \
  -framework Cocoa \
  -framework Security \
  -framework WebKit \
  "$SCRIPT_DIR/KouziMailAssistant.swift" \
  -o "$APP_BUILD_PATH/Contents/MacOS/$APP_NAME"

cp "$SCRIPT_DIR/Info.plist" "$APP_BUILD_PATH/Contents/Info.plist"
cp "$ICON_FILE" "$APP_BUILD_PATH/Contents/Resources/AppIcon.icns"

chmod -R u+w "$APP_BUILD_PATH"
xattr -cr "$APP_BUILD_PATH"

if [[ -n "$DOTNET_BROTLI_LIB_DIR" ]]; then
  for modified_library in "${modified_libraries[@]}"; do
    codesign --force --sign - --timestamp=none "$modified_library"
    codesign --verify --strict --verbose=2 "$modified_library"
  done
fi

codesign --force --deep --sign - --timestamp=none "$APP_BUILD_PATH"
codesign --verify --deep --strict --verbose=2 "$APP_BUILD_PATH"

mkdir -p "$APP_OUTPUT_DIR"
rm -rf "$APP_PATH"
ditto --noextattr --noqtn "$APP_BUILD_PATH" "$APP_PATH"

if (( APP_ONLY )); then
  xattr -cr "$APP_PATH"
  codesign --verify --deep --strict --verbose=2 "$APP_PATH"
  print "$APP_PATH"
  exit 0
fi

# File Provider can attach FinderInfo to the workspace copy after it lands. The
# pristine temporary app was strictly verified above and is used for DMG staging.
codesign --verify --deep --verbose=2 "$APP_PATH"

STAGING_DIR="$WORK_DIR/dmg-root"
mkdir -p "$STAGING_DIR"
ditto --noextattr --noqtn "$APP_BUILD_PATH" "$STAGING_DIR/$APP_NAME.app"
codesign --verify --deep --strict --verbose=2 "$STAGING_DIR/$APP_NAME.app"
ln -s /Applications "$STAGING_DIR/Applications"
hdiutil create \
  -volname "$APP_NAME" \
  -srcfolder "$STAGING_DIR" \
  -ov \
  -format UDZO \
  "$DMG_PATH"

print "$DMG_PATH"

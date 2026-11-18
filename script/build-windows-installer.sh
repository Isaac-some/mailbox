#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$ROOT_DIR/windows-app/MailAssistant.Windows.csproj" | head -n 1)"
PACKAGE_DIR="${1:-$ROOT_DIR/windows-app/build/MailAssistant-Windows-x64-v$APP_VERSION}"
OUTPUT_FILE="${2:-$ROOT_DIR/windows-app/build/MailAssistant-Windows-x64-v$APP_VERSION-Setup.exe}"
MAKENSIS_BIN="${MAKENSIS_BIN:-makensis}"
if ! command -v "$MAKENSIS_BIN" >/dev/null 2>&1; then
  printf '%s\n' "缺少 NSIS。Mac 可运行 brew install makensis，再重新打包。" >&2
  exit 2
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT
python3 - "$PACKAGE_DIR" "$WORK_DIR/uninstall-files.nsh" <<'PY'
import sys
from pathlib import Path
package, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2])
for relative in ('邮箱助手.exe', 'server/MailArchiver.exe', 'server/appsettings.Local.json', 'server/updater/MailAssistant.Updater.exe'):
    if not (package / relative).is_file():
        raise SystemExit('完整 Windows 包缺少文件：' + relative)
def nsis_path(path):
    return str(path.relative_to(package)).replace('\\', '/').replace('$', '$$').replace('"', '$\\"').replace('/', '\\')
files = sorted(p for p in package.rglob('*') if p.is_file())
directories = sorted((p for p in package.rglob('*') if p.is_dir()), key=lambda p: len(p.parts), reverse=True)
commands = [f'Delete "$INSTDIR\\{nsis_path(p)}"' for p in files]
commands += [f'RMDir "$INSTDIR\\{nsis_path(p)}"' for p in directories]
output.write_text('\n'.join(commands) + '\n', encoding='utf-8')
PY
mkdir -p "$(dirname "$OUTPUT_FILE")"
"$MAKENSIS_BIN" -V3 "-DPACKAGE_DIR=$PACKAGE_DIR" "-DOUTPUT_FILE=$OUTPUT_FILE" \
  "-DAPP_VERSION=$APP_VERSION" "-DUNINSTALL_FILES=$WORK_DIR/uninstall-files.nsh" \
  "$ROOT_DIR/windows-app/installer.nsi"
printf '%s\n' "$OUTPUT_FILE"

#!/usr/bin/env python3
"""Verify source and actual binary versions, installers, signatures and release provenance."""
import argparse
import hashlib
import json
import plistlib
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path
from windows_pe_version import read_version

root = Path(__file__).resolve().parent.parent
p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--assets', type=Path, required=True)
p.add_argument('--test-report', type=Path, required=True)
args = p.parse_args()
assets = args.assets.resolve()
def run(command):
    return subprocess.check_output([str(x) for x in command], cwd=root, stderr=subprocess.STDOUT, text=True)
def sha(path):
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1024 * 1024), b''): h.update(block)
    return h.hexdigest()
def require(value, message):
    if not value: raise SystemExit(message)
def version_of(project):
    return ET.parse(project).getroot().findtext('./PropertyGroup/Version')
version = version_of(root / 'MailArchiver.csproj')
for project in (root / 'MailArchiver.csproj', root / 'windows-app/MailAssistant.Windows.csproj'):
    xml = ET.parse(project).getroot()
    for key, expected in [('Version', version), ('AssemblyVersion', version + '.0'), ('FileVersion', version + '.0')]:
        require(xml.findtext('./PropertyGroup/' + key) == expected, f'{project.name}: {key} differs from {version}')
require(plistlib.loads((root / 'local-app/Info.plist').read_bytes())['CFBundleShortVersionString'] == version, 'Source plist version mismatch')
require(json.loads((root / 'appsettings.json').read_text())['ReleaseNotes']['AppVersion'] == version, 'Source display version mismatch')
require(not run(['git', 'status', '--porcelain']).strip(), 'Commit the reviewed source before producing release provenance.')
source_commit = run(['git', 'rev-parse', 'HEAD']).strip()
manifest = json.loads((assets / 'desktop-update.json').read_text())
require(manifest['version'] == version and manifest['tag'] == 'v' + version and not manifest['prerelease'], 'Signed release version/tag mismatch')
run(['openssl', 'dgst', '-sha256', '-verify', root / 'DesktopUpdate/update-public-key.pem', '-signature', assets / 'desktop-update.sig', assets / 'desktop-update.json'])
for a in manifest['artifacts']:
    file = assets / a['fileName']
    require(file.stat().st_size == a['size'] and sha(file) == a['sha256'], 'Signed archive hash mismatch: ' + a['fileName'])
    require(a['url'] == f'https://github.com/Isaac-some/mailbox-releases/releases/download/v{version}/{file.name}', 'Signed release URL mismatch')
mac = root / f'local-app/build-v{version}/邮箱助手.app'
win = root / f'windows-app/build/MailAssistant-Windows-x64-v{version}'
setup = assets / f'MailAssistant-Windows-x64-v{version}-Setup.exe'
paths = [mac / 'Contents/Resources/server/MailArchiver.dll', win / 'server/MailArchiver.dll']
output = run([root / '.local-tools/dotnet/dotnet', 'run', '--project', root / 'tests/ReleaseVersionReader/ReleaseVersionReader.csproj', '--verbosity', 'quiet', '--', *paths])
binaries = json.loads(output.strip().splitlines()[-1]) + [read_version(win / '邮箱助手.exe'), read_version(setup)]
for index, row in enumerate(binaries):
    require(row['fileVersion'] == version + '.0', 'Actual file version mismatch: ' + row['path'])
    if index < 2: require(row['assemblyVersion'] == version + '.0', 'Actual backend assembly version mismatch')
    if index < 3: require(source_commit in (row['productVersion'] or ''), 'Actual binary does not identify the reviewed source commit: ' + row['path'])
for server in (mac / 'Contents/Resources/server', win / 'server'):
    forbidden = {'network-settings.json', 'mail-domain-profiles.json', 'upstream-mailbox-connection.json', 'credential-encryption.key', 'credential_encryption_key', 'update-signing-key.pem'}
    require(not any(f.is_file() and (f.name in forbidden or f.suffix in ('.db', '.sqlite')) for f in server.rglob('*')), 'A private device file entered the package')
    for setting in server.glob('appsettings*.json'):
        upstream = json.loads(setting.read_text()).get('UpstreamMailboxSync', {})
        require(not upstream.get('Enabled') and not upstream.get('Endpoint') and not upstream.get('BearerToken'), 'A platform credential/source entered the package')
        data = json.loads(setting.read_text())
        if 'ReleaseNotes' in data: require(data['ReleaseNotes']['AppVersion'] == version, 'Packaged display version mismatch')
with tempfile.TemporaryDirectory(prefix='mailbox-release-verification-') as temporary:
    temp = Path(temporary); mount = temp / 'mounted'; mount.mkdir()
    run(['hdiutil', 'attach', '-readonly', '-nobrowse', '-mountpoint', mount, assets / f'MailAssistant-AppleSilicon-v{version}.dmg'])
    try:
        require(sorted(x.name for x in mount.iterdir()) == ['Applications', '邮箱助手.app'], 'DMG contains extra documents/files')
        require((mount / 'Applications').is_symlink(), 'DMG installation shortcut missing')
        mounted_app = mount / '邮箱助手.app'
        run(['codesign', '--verify', '--deep', '--strict', mounted_app])
        require(plistlib.loads((mounted_app / 'Contents/Info.plist').read_bytes())['CFBundleShortVersionString'] == version, 'DMG actual app version mismatch')
        with zipfile.ZipFile(assets / f'MailAssistant-AppleSilicon-v{version}-update.zip') as z:
            require(z.testzip() is None, 'Mac update ZIP CRC failure')
            for relative in ('Contents/Info.plist', 'Contents/MacOS/邮箱助手', 'Contents/Resources/server/MailArchiver.dll'):
                require(hashlib.sha256(z.read('邮箱助手.app/' + relative)).hexdigest() == sha(mounted_app / relative), 'DMG differs from update archive: ' + relative)
    finally: run(['hdiutil', 'detach', mount])
    run(['7zz', 't', setup])
    extracted = temp / 'installer'
    run(['7zz', 'x', '-y', '-o' + str(extracted), setup])
    files = [f for f in win.rglob('*') if f.is_file()]
    with zipfile.ZipFile(assets / f'MailAssistant-Windows-x64-v{version}.zip') as z:
        require(z.testzip() is None, 'Windows ZIP CRC failure')
        for f in files:
            relative = f.relative_to(win)
            require((extracted / relative).is_file() and sha(extracted / relative) == sha(f), 'Installer omitted/changed: ' + str(relative))
            require(hashlib.sha256(z.read(win.name + '/' + relative.as_posix())).hexdigest() == sha(f), 'ZIP differs from installer: ' + str(relative))
trx = ET.parse(args.test_report).getroot()
counters = next(e for e in trx.iter() if e.tag.endswith('Counters')).attrib
require(int(counters['failed']) == 0 and int(counters['passed']) == int(counters['total']), 'Regression suite did not fully pass')
all_assets = [f for f in assets.iterdir() if f.is_file() and f.name not in ('SHA256SUMS.txt', 'build-manifest.json')]
provenance = dict(appVersion=version, sourceRepository='https://github.com/Isaac-some/mailbox', sourceCommit=source_commit,
    sourceTag='v' + version, artifacts={f.name:dict(sha256=sha(f), size=f.stat().st_size) for f in sorted(all_assets)},
    validation=dict(regression={k:int(counters[k]) for k in ('total','passed','failed')}, binaryVersionsAligned=True,
        dmgContainsOnlyApplicationAndShortcut=True, macDmgMatchesUpdateZip=True, installerPayloadFiles=len(files),
        allWindowsInstallerAndZipFilesMatch=True), macSigning='ad-hoc; not notarized')
(assets / 'build-manifest.json').write_text(json.dumps(provenance, ensure_ascii=False, indent=2) + '\n')
(assets / 'SHA256SUMS.txt').write_text(''.join(f'{sha(f)}  {f.name}\n' for f in sorted(assets.iterdir()) if f.is_file() and f.name != 'SHA256SUMS.txt'))
print(json.dumps(dict(result='PASS', version=version, sourceCommit=source_commit, windowsPayloadFiles=len(files), regressionPassed=int(counters['passed'])), indent=2))

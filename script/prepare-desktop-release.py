#!/usr/bin/env python3
"""Collect the installer and signed updater assets; does not publish a release."""
import argparse
import hashlib
import re
import shutil
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parent.parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--notes-file', type=Path)
args = parser.parse_args()
version = re.search(r'<Version>([^<]+)</Version>', (root / 'MailArchiver.csproj').read_text()).group(1)
mac = root / f'local-app/build-v{version}'
windows = root / 'windows-app/build'
assets = [mac / f'MailAssistant-AppleSilicon-v{version}.dmg',
          mac / f'MailAssistant-AppleSilicon-v{version}-update.zip',
          windows / f'MailAssistant-Windows-x64-v{version}.zip',
          windows / f'MailAssistant-Windows-x64-v{version}-Setup.exe']
for asset in assets:
    if not asset.is_file():
        raise SystemExit('Build both platforms first; missing: ' + str(asset))
output = args.output.resolve()
if output.exists() and any(output.iterdir()):
    raise SystemExit('Refusing to overwrite a nonempty release directory.')
output.mkdir(parents=True, exist_ok=True)
for asset in assets:
    shutil.copy2(asset, output / asset.name)
command = ['python3', str(root / 'script/create-update-manifest.py'), '--version', version,
           '--tag', 'v' + version, '--mac', str(output / assets[1].name),
           '--windows', str(output / assets[2].name), '--output', str(output)]
if args.notes_file:
    command += ['--notes-file', str(args.notes_file.resolve())]
subprocess.run(command, check=True)
checksums = [f'{hashlib.sha256(asset.read_bytes()).hexdigest()}  {asset.name}'
             for asset in sorted(output.iterdir()) if asset.is_file()]
(output / 'SHA256SUMS.txt').write_text('\n'.join(checksums) + '\n')
print('Prepared local release assets: ' + str(output))
print('Publish all six installer/update assets together as a latest, non-prerelease GitHub release.')

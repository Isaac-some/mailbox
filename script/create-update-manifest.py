#!/usr/bin/env python3
"""Sign desktop update packages. The private key never enters the repository or packages."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('--version', required=True)
p.add_argument('--tag', required=True)
p.add_argument('--mac', type=Path, required=True)
p.add_argument('--windows', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
p.add_argument('--prerelease', action='store_true')
p.add_argument('--notes-file', type=Path)
p.add_argument('--key', type=Path, default=Path.home() / 'Library/Application Support/MailAssistantRelease/update-signing-key.pem')
args = p.parse_args()
public = subprocess.check_output(['openssl', 'pkey', '-in', str(args.key), '-pubout'])
expected = (Path(__file__).resolve().parent.parent / 'DesktopUpdate/update-public-key.pem').read_bytes()
if public.strip() != expected.strip():
    raise SystemExit('Signing key does not match the public key compiled into the app.')
args.output.mkdir(parents=True, exist_ok=True)
artifacts = []
for platform, package, root in [('osx-arm64', args.mac, '邮箱助手.app'),
                                ('win-x64', args.windows, f'MailAssistant-Windows-x64-v{args.version}')]:
    artifacts.append(dict(platform=platform, fileName=package.name,
        url=f'https://github.com/Isaac-some/mailbox-releases/releases/download/{args.tag}/{package.name}',
        size=package.stat().st_size, sha256=hashlib.sha256(package.read_bytes()).hexdigest(), rootDirectory=root))
manifest = args.output / 'desktop-update.json'
manifest.write_text(json.dumps(dict(schema=1, version=args.version, tag=args.tag,
    prerelease=args.prerelease, artifacts=artifacts, notes=args.notes_file.read_text() if args.notes_file else None), ensure_ascii=False, indent=2) + '\n')
subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(args.key), '-out',
    str(args.output / 'desktop-update.sig'), str(manifest)], check=True)
print(f'Signed update manifest for {args.tag}.')

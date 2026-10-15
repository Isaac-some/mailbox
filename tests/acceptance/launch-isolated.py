"""Start the real desktop server with isolated storage; never print credentials."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys

repo = Path(__file__).resolve().parents[2]
root = repo / '.runtime' / 'acceptance-20261015' / sys.argv[1]
root.mkdir(parents=True, exist_ok=True)
root.chmod(0o700)
key = root / 'credential-encryption.key'
if not key.exists():
    key.write_text(base64.b64encode(os.urandom(32)).decode())
    key.chmod(0o600)
bundle = repo / 'local-app/build-v2.3.5/邮箱助手.app/Contents/Resources/server'
config = json.loads((bundle / 'appsettings.Local.json').read_text())
env = os.environ.copy()
env.update({
    'ASPNETCORE_ENVIRONMENT': 'Local',
    'ASPNETCORE_URLS': 'http://127.0.0.1:' + (sys.argv[2] if len(sys.argv) > 2 else '5127'),
    'MAIL_ASSISTANT_LOCAL_APP': '1',
    'MAIL_ASSISTANT_DATA_DIRECTORY': str(root),
    'ConnectionStrings__DefaultConnection': f'Data Source={root / "mail-archive.sqlite"}',
    'CredentialEncryption__KeyFilePath': str(key),
    'DataProtection__KeyPath': str(root / 'keys'),
    'UpstreamMailboxSync__BearerToken': config['UpstreamMailboxSync']['BearerToken'],
    'Logging__LogLevel__Default': 'Warning',
    'Logging__LogLevel__Microsoft': 'Warning',
})
assembly = repo / 'bin/Release/net10.0/MailArchiver.dll'
with (root / 'server.log').open('a') as log:
    child = subprocess.Popen([str(repo / '.local-tools/dotnet/dotnet'), str(assembly)],
                             cwd=repo, env=env, stdout=log, stderr=log)
    (root / 'server.pid').write_text(str(child.pid))
    print(f'Isolated server started: {root.name}, port {env["ASPNETCORE_URLS"].split(":")[-1]}')
    try:
        sys.exit(child.wait())
    except KeyboardInterrupt:
        child.terminate()
        child.wait(timeout=20)

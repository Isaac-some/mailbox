#!/usr/bin/env python3
"""Exercise the shipped updater against public releases, using only fresh isolated storage."""
import argparse
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

p = argparse.ArgumentParser()
p.add_argument('--package', type=Path, required=True)
p.add_argument('--root', type=Path, required=True)
p.add_argument('--target-tag', required=True)
p.add_argument('--expected-version', required=True)
p.add_argument('--port', type=int, default=5162)
p.add_argument('--scenario', choices=['success', 'corrupt', 'rollback'], default='success')
args = p.parse_args()
root = args.root.resolve()
if root.exists():
    raise SystemExit('Refusing to reuse an existing test directory.')
root.mkdir(parents=True)
data = root / 'data'
data.mkdir()
installation = root / 'installation'
installation.mkdir()
mac = sys.platform == 'darwin'
if mac:
    subprocess.run(['/usr/bin/ditto', '-x', '-k', str(args.package.resolve()), str(installation)], check=True)
    installed = installation / '邮箱助手.app'
else:
    with zipfile.ZipFile(args.package) as archive:
        archive.extractall(installation)
    installed = next(installation.iterdir())
base = f'http://127.0.0.1:{args.port}'
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

def get(path, timeout=5):
    with opener.open(base + path, timeout=timeout) as response:
        return response.read()

def status():
    return json.loads(get('/DesktopUpdates/Status'))

def until(predicate, seconds, description):
    deadline = time.monotonic() + seconds
    last = None
    while time.monotonic() < deadline:
        try:
            last = predicate()
            if last:
                return last
        except (OSError, ValueError, urllib.error.URLError):
            pass
        time.sleep(.25)
    raise AssertionError(f'Timed out: {description}; last={last!r}')

def post(action, timeout=150):
    page = get('/DesktopUpdates').decode()
    token = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page).group(1)
    request = urllib.request.Request(base + '/DesktopUpdates/' + action,
        urllib.parse.urlencode({'__RequestVerificationToken': token}).encode(), method='POST')
    with opener.open(request, timeout=timeout) as response:
        return json.loads(response.read())

def seed():
    with sqlite3.connect(data / 'mail-archive.sqlite', timeout=30) as connection:
        connection.execute('''INSERT INTO MailAccounts
            (Name,EmailAddress,UseSSL,LastSync,IsEnabled,ExcludedFolders,Provider,
             PreferredIncomingAuth,PreferredOutgoingAuth,CredentialKind,CredentialScope,MailboxSyncLookbackDays,ExpandedMailboxCategories)
            VALUES ('update-fixture','update-fixture@example.invalid',1,'2026-01-01 00:00:00',0,'','IMAP',
                    'Unknown','Unknown','Unknown','Unknown',7,0)''')
        account = connection.execute('SELECT last_insert_rowid()').fetchone()[0]
        user = connection.execute('SELECT Id FROM Users LIMIT 1').fetchone()[0]
        connection.execute('INSERT INTO UserMailAccounts (UserId,MailAccountId) VALUES (?,?)', (user, account))
        connection.execute('''INSERT INTO ArchivedEmails
            (MailAccountId,MessageId,Subject,Body,HtmlBody,"From","To",Cc,Bcc,SentDate,ReceivedDate,IsOutgoing,HasAttachments,FolderName,FolderCategory)
            VALUES (?, 'update-preservation@example.invalid','更新保留验证','synthetic mail body 123','',
                    'sender@example.invalid','update-fixture@example.invalid','','','2026-01-01 00:00:00',
                    '2026-01-01 00:00:00',0,0,'INBOX','Inbox')''', (account,))
    (data / 'update-preservation-sentinel.json').write_text('{"setting":"preserve-exactly"}')
    (data / 'network-settings.json').write_text(json.dumps(dict(Mode=0, Type=None, Host=None,
        Port=None, Username=None, EncryptedPassword=None, Version=424242)))

def snapshot():
    with sqlite3.connect(data / 'mail-archive.sqlite', timeout=30) as connection:
        return dict(accounts=connection.execute('SELECT Id,Name,EmailAddress,IsEnabled FROM MailAccounts').fetchall(),
            emails=connection.execute('SELECT Id,MessageId,Subject,Body FROM ArchivedEmails').fetchall(),
            users=connection.execute('SELECT Id,Username FROM Users').fetchall(),
            key=hashlib.sha256((data / 'credential-encryption.key').read_bytes()).hexdigest(),
            settings=(data / 'update-preservation-sentinel.json').read_text(),
            networkSettings=(data / 'network-settings.json').read_text())

environment = os.environ.copy()
environment.update(MAIL_ASSISTANT_DATA_DIRECTORY=str(data), MAIL_ASSISTANT_PORT=str(args.port),
    MAIL_ASSISTANT_UPDATE_TEST_TAG=args.target_tag, UpstreamMailboxSync__Enabled='false',
    OutboundMail__Enabled='false', Logging__LogLevel__Default='Warning',
    MAIL_ASSISTANT_UPDATE_PID_FILE=str(root / 'initial-native.pid'))
native = None
try:
    if mac:
        command = ['/usr/bin/open', '-n', '-g', '-j', '-o', str(root / 'native.log'), '--stderr', str(root / 'native-errors.log')]
        for name, value in environment.items():
            if name.startswith('MAIL_ASSISTANT_') or '__' in name:
                command += ['--env', name + '=' + value]
        subprocess.run(command + [str(installed)], check=True)
    else:
        native = subprocess.Popen([str(installed / '邮箱助手.exe')], env=environment,
            stdout=(root / 'native.log').open('w'), stderr=subprocess.STDOUT)
    until(lambda: status(), 90, 'initial native readiness')
    original_version = status()['currentVersion']
    print('Native ready:', original_version, 'scenario:', args.scenario, flush=True)
    original_pid = json.loads(get('/DesktopUpdates/Health'))['processId']
    seed()
    before = snapshot()
    try:
        opener.open(urllib.request.Request(base + '/DesktopUpdates/Install', b'', method='POST'))
        raise AssertionError('Missing CSRF token was accepted')
    except urllib.error.HTTPError as error:
        assert error.code == 400
    checked = post('Check')
    assert checked['phase'] == 'available', checked
    assert checked['latestVersion'] == args.expected_version, checked
    assert post('Download')['phase'] == 'downloading'
    downloaded = until(lambda: (s if (s := status())['phase'] in ('ready', 'error') else None), 1900, 'real public download')
    assert downloaded['phase'] == 'ready', downloaded
    print('Public download verified:', downloaded['downloaded'], 'bytes', flush=True)
    session = next(path for path in (data / 'updates').iterdir() if (path / 'archive.zip').exists())
    # An in-progress send must block installation without quitting the app. The worker is disabled.
    with sqlite3.connect(data / 'mail-archive.sqlite', timeout=30) as connection:
        user = before['users'][0][0]
        account = before['accounts'][0][0]
        connection.execute("INSERT INTO OutboundMailTasks (CreatedByUserId,Name,CreatedAtUtc) VALUES (?,'update-busy-fixture','2026-01-01 00:00:00')", (user,))
        task = connection.execute('SELECT last_insert_rowid()').fetchone()[0]
        connection.execute('''INSERT INTO OutboundMailTaskItems
            (OutboundMailTaskId,MailAccountId,CsvRowNumber,ScheduledAtUtc,Recipient,Subject,Body,Status)
            VALUES (?,?,1,'2099-01-01 00:00:00','fixture@example.invalid','synthetic','synthetic','Sending')''', (task, account))
    busy = post('Install')
    assert busy['phase'] == 'ready' and '后台任务' in busy['message'], busy
    assert json.loads(get('/DesktopUpdates/Health'))['processId'] == original_pid
    with sqlite3.connect(data / 'mail-archive.sqlite', timeout=30) as connection:
        connection.execute('DELETE FROM OutboundMailTaskItems WHERE OutboundMailTaskId=?', (task,))
        connection.execute('DELETE FROM OutboundMailTasks WHERE Id=?', (task,))
    if args.scenario == 'corrupt':
        with (session / 'archive.zip').open('r+b') as stream:
            stream.seek(-1, 2)
            old = stream.read(1)
            stream.seek(-1, 2)
            stream.write(bytes([old[0] ^ 255]))
        result = post('Install')
        assert result['phase'] == 'ready', result
        assert status()['currentVersion'] == original_version
        assert json.loads(get('/DesktopUpdates/Health'))['processId'] == original_pid
    else:
        outcome = {}
        def install():
            try:
                outcome['response'] = post('Install')
            except Exception as error:
                outcome['exception'] = type(error).__name__
        thread = threading.Thread(target=install)
        thread.start()
        if args.scenario == 'rollback':
            until(lambda: (session / 'installer-ready').exists(), 150, 'validated staging')
            plan = json.loads((session / 'plan.json').read_text())
            manifest = json.loads((session / 'desktop-update.json').read_text())
            artifact = next(a for a in manifest['artifacts'] if a['platform'] == ('osx-arm64' if mac else 'win-x64'))
            staged = installation / ('.mail-assistant-update-' + plan['nonce']) / artifact['rootDirectory']
            launcher = staged / 'Contents/MacOS/邮箱助手' if mac else staged / '邮箱助手.exe'
            launcher.write_bytes(b'intentionally invalid isolated test executable')
        thread.join(160)
        assert not thread.is_alive(), 'Install HTTP request did not finish'
        result = until(lambda: json.loads((data / 'update-result.json').read_text())
            if (data / 'update-result.json').exists() else None, 240, 'installer completion')
        expected = args.expected_version if args.scenario == 'success' else original_version
        until(lambda: status()['currentVersion'] == expected, 90, 'restarted app')
        assert result['succeeded'] == (args.scenario == 'success'), result
        if args.scenario == 'rollback':
            assert result['rolledBack'], result
        else:
            assert json.loads(get('/DesktopUpdates/Health'))['processId'] != original_pid
    assert snapshot() == before, 'Account, email, key or settings changed during the update'
    report = dict(result='PASS', platform=sys.platform, scenario=args.scenario, fromVersion=original_version,
        toVersion=status()['currentVersion'], targetTag=args.target_tag, accountCount=len(before['accounts']),
        emailCount=len(before['emails']), dataPreserved=True, csrfRejected=True, publicDownload=True,
        activeTaskBlocked=True, networkSettingsPreserved=True)
    (root / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2))
    print(json.dumps(report, ensure_ascii=False), flush=True)
finally:
    # Kill only PIDs written by this test's processes; never stop an application by name.
    server_pid = None
    try:
        server_pid = json.loads(get('/DesktopUpdates/Health'))['processId']
    except (OSError, ValueError, urllib.error.URLError):
        pass
    pid_files = [root / 'initial-native.pid'] + list(data.glob('updates/*/new-native.pid'))
    for pid_file in pid_files:
        try:
            pid = int(pid_file.read_text())
            if mac:
                os.kill(pid, 15)
            else:
                subprocess.run(['taskkill', '/PID', str(pid), '/T', '/F'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except (OSError, ValueError):
            pass
    if mac and server_pid:
        try:
            os.kill(server_pid, 15)
        except OSError:
            pass

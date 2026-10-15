"""Send marked test mail only between verified accounts in the isolated test DB.

Uses a durable attempt ledger: an ambiguous network result is never retried.
"""
import hashlib
from html.parser import HTMLParser
import http.cookiejar
import json
from pathlib import Path
import sqlite3
import sys
import time
import urllib.request
import uuid

repo = Path(__file__).resolve().parents[2]
root = repo / '.runtime/acceptance-20261015'
base = sys.argv[1]
database = root / sys.argv[2] / 'mail-archive.sqlite'
if '--execute' not in sys.argv:
    raise SystemExit('An explicit --execute is required for live sending.')
samples = json.loads((root / 'samples.json').read_text())
connection = sqlite3.connect(database)
verified = []
for sample in samples:
    row = connection.execute('SELECT Id,CredentialScope FROM MailAccounts WHERE EmailAddress=? COLLATE NOCASE', (sample['email'],)).fetchone()
    if row and row[1] == 'ImapAndSmtp':
        verified.append(dict(sample, accountId=row[0]))
if len(verified) < 2:
    raise SystemExit('Need at least two verified sending and receiving accounts.')
ledger_file = root / 'send-ledger.json'
ledger = json.loads(ledger_file.read_text()) if ledger_file.exists() else []

class Inputs(HTMLParser):
    token = None
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == 'input' and attrs.get('name') == '__RequestVerificationToken':
            self.token = attrs.get('value')

for index, sender in enumerate(verified):
    recipient = verified[(index + 1) % len(verified)]
    pair = f'{sender["accountId"]}:{recipient["accountId"]}'
    sender_fingerprint = hashlib.sha256(sender['email'].lower().encode()).hexdigest()
    if any(item.get('senderFingerprint') == sender_fingerprint for item in ledger):
        continue
    subject = 'MAILBOX-ACCEPT-20261015-' + uuid.uuid4().hex[:10]
    attachment = ('Mailbox acceptance attachment\n中文附件完整性验证\n' + subject).encode()
    record = dict(pair=pair, senderId=sender['accountId'], recipientId=recipient['accountId'],
                  senderFingerprint=sender_fingerprint, environment=sys.argv[2],
                  senderProvider=sender['provider'], recipientProvider=recipient['provider'],
                  subject=subject, attachmentSha256=hashlib.sha256(attachment).hexdigest(), status='attempting')
    opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    with opener.open(f'{base}/OutboundMail?accountId={sender["accountId"]}', timeout=30) as response:
        parser = Inputs(); parser.feed(response.read().decode())
    if not parser.token:
        raise SystemExit('Compose form token missing.')
    boundary = 'MailboxTest' + uuid.uuid4().hex
    chunks = []
    for name, value in dict(AccountId=sender['accountId'], To=recipient['email'], Cc='', Subject=subject,
                            Body='邮箱助手真实互发验收。请核对主题、中文正文、附件。\n' + subject,
                            __RequestVerificationToken=parser.token).items():
        chunks.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
    chunks.append(f'--{boundary}\r\nContent-Disposition: form-data; name="Attachments"; filename="acceptance.txt"\r\nContent-Type: text/plain\r\n\r\n'.encode() + attachment + b'\r\n')
    chunks.append(f'--{boundary}--\r\n'.encode())
    ledger.append(record)
    ledger_file.write_text(json.dumps(ledger, indent=2))
    started = time.monotonic()
    try:
        request = urllib.request.Request(base + '/OutboundMail', data=b''.join(chunks),
            headers={'Content-Type': 'multipart/form-data; boundary=' + boundary}, method='POST')
        with opener.open(request, timeout=120) as response:
            page = response.read().decode()
            record['status'] = 'sent' if '/Emails' in response.url else 'rejected'
            record['sentCopyWarning'] = '副本保存失败' in page
            if record['status'] == 'rejected':
                (root / ('send-error-' + subject + '.html')).write_text(page)
    except Exception as error:
        record['status'] = 'unknown_no_retry'
        record['error'] = type(error).__name__
    record['seconds'] = round(time.monotonic() - started, 2)
    ledger_file.write_text(json.dumps(ledger, indent=2))
    print(json.dumps(record), flush=True)

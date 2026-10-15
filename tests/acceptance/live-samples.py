"""Exercise the real app endpoints against an explicit, private sample list."""
import concurrent.futures
import http.cookiejar
import json
from pathlib import Path
import re
import sys
import time
import urllib.parse
import urllib.request

repo = Path(__file__).resolve().parents[2]
root = repo / '.runtime/acceptance-20261015'
base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:5129'
samples = json.loads((root / (sys.argv[2] if len(sys.argv) > 2 else 'samples.json')).read_text())
result_name = sys.argv[3] if len(sys.argv) > 3 else 'live-results.json'
# Let reachable providers run before a network-blocked provider can occupy both slots.
samples.sort(key=lambda x: (['Yahoo', 'Outlook', 'Gmx', 'Gmail'].index(x['provider'])))
results = []
for i, sample in enumerate(samples):
    label = sample['provider'] + '-' + str(i % 3 + 1)
    started = time.monotonic()
    try:
        request = base + '/MailAccounts?' + urllib.parse.urlencode({'q': sample['email'], 'priorityLookup': 'true'})
        with urllib.request.urlopen(request, timeout=45) as response:
            page = response.read().decode()
        account_id = re.search(r'data-account-id="(\d+)"', page)
        result = {'sample': label, 'provider': sample['provider'], 'accountId': int(account_id[1]) if account_id else None,
                  'searchSeconds': round(time.monotonic() - started, 3),
                  'priorityQueued': 'data-priority-account=' in page}
    except Exception as error:
        result = {'sample': label, 'provider': sample['provider'], 'error': type(error).__name__}
    results.append(result)
    print(json.dumps(result), flush=True)

pending = {r['accountId']: r for r in results if r.get('accountId') and r.get('priorityQueued')}
deadline = time.monotonic() + 7 * 60
while pending and time.monotonic() < deadline:
    for account_id, result in list(pending.items()):
        try:
            with urllib.request.urlopen(f'{base}/MailAccounts/SyncStatus/{account_id}', timeout=10) as response:
                status = json.load(response)
            job = status.get('job')
            if status.get('state') == 'NotQueued' and job and job['status'] in ['Completed', 'Failed', 'Cancelled', 'RateLimited']:
                result.update(status=job['status'], errorCode=job.get('errorCode'), newEmails=job.get('newEmails'))
                pending.pop(account_id)
                print(json.dumps(result), flush=True)
        except Exception as error:
            result['pollError'] = type(error).__name__
    (root / result_name).write_text(json.dumps(results, indent=2))
    if pending:
        time.sleep(2)
for result in pending.values():
    result['status'] = 'StillRunningAtDeadline'
(root / result_name).write_text(json.dumps(results, indent=2))
print('Sample run finished', flush=True)

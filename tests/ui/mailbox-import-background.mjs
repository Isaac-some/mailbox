import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href : 'playwright');
const base = process.env.MAILBOX_BASE_URL || 'http://127.0.0.1:5147';
const browser = await chromium.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', headless: true });
try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(base + '/MailAccounts/ImportCsv');
    await page.locator('#accountFileInput').setInputFiles({
        name: 'import-feedback.csv', mimeType: 'text/csv', buffer: Buffer.from('email,domain,credential,client_id\nreader-one@gmail.com,gmail.com,short,\nreader-two@gmail.com,gmail.com,short,\nnot-an-email,gmail.com,short,\n')
    });
    await page.locator('#saveBtn').click();
    const panel = page.locator('#csvImportTasksList');
    await panel.getByRole('button', { name: '后台验证', exact: true }).waitFor();
    assert.match(await panel.innerText(), /导入成功 2 · 导入失败 1/);
    await panel.getByRole('button', { name: '后台验证', exact: true }).click();
    await panel.getByText('已在后台执行自动验证。直接打开邮箱将优先验证并获取邮件。', { exact: true }).waitFor();
    await page.waitForTimeout(2000);
    const text = await panel.innerText();
    assert.match(text, /导入成功 2 · 导入失败 1/);
    assert.doesNotMatch(text, /待验证|格式待确认|网络\/TLS|认证 \d|验证进度/);
    const ids = await page.evaluate(() => JSON.parse(sessionStorage.getItem('mailAssistant.csvImportJobs')));
    const data = await (await page.request.get(base + '/MailAccounts/CsvImportStatusJson?jobId=' + ids[0])).json();
    assert.equal(data.failed, 1);
    assert.equal(data.status, 'CompletedWithErrors');
    assert.equal(data.verificationStarted, true);
    assert.equal(Object.keys(data).filter(key => /^verification/.test(key)).join(','), 'verificationStarted');
    await panel.getByRole('link', { name: '查看导入结果' }).click();
    const body = await page.locator('main').innerText();
    assert.doesNotMatch(body, /已验证成功|验证失败 \d|16 位|网络\/TLS|字段警告/);
    assert.match(body, /not-an-email/);
    await page.goto(base + '/LocalMaintenance');
    assert.match(await page.locator('main').innerText(), /跟随这台电脑当前的系统网络与 VPN/);
    assert.equal(await page.locator('#proxy-host').count(), 0);
    assert.doesNotMatch(await page.locator('main').innerText(), /retired-proxy\.invalid/);

    const actions = [];
    let downloaded = false, installed = false;
    const state = phase => ({ phase, currentVersion: process.env.MAILBOX_EXPECTED_VERSION || '2.4.4', latestVersion: '9.9.9', message: '', total: 100, downloaded: phase === 'ready' ? 100 : 0 });
    for (const [name, phase] of [['Check', 'available'], ['Download', 'downloading'], ['Install', 'installing']]) {
        await page.route('**/DesktopUpdates/' + name, route => {
            actions.push(name);
            if (name === 'Download') downloaded = true;
            if (name === 'Install') installed = true;
            return route.fulfill({ json: state(phase) });
        });
    }
    await page.route('**/DesktopUpdates/Status', route => route.fulfill({ json: state(installed ? 'installing' : downloaded ? 'ready' : 'idle') }));
    await page.goto(base + '/DesktopUpdates');
    for (let i = 0; i < 80 && !installed; i++) await page.waitForTimeout(250);
    assert.deepEqual(actions, ['Check', 'Download', 'Install']);
    const token = await page.locator('#update-actions input[name="__RequestVerificationToken"]').inputValue();
    // APIRequestContext bypasses the browser mocks: this checks the actual GitHub signed release.
    let live = null;
    if (process.env.MAILBOX_LIVE_UPDATE_CHECK === '1') {
    live = await (await page.request.post(base + '/DesktopUpdates/Check', { form: { __RequestVerificationToken: token }, timeout: 130000 })).json();
    assert.ok(['current', 'ahead'].includes(live.phase), JSON.stringify(live));
    assert.equal(live.currentVersion, process.env.MAILBOX_EXPECTED_VERSION || '2.4.4');
    assert.ok(live.latestVersion);
    }
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ result: 'PASS', importSaved: 2, importFailed: 1, verificationDoesNotChangeImport: true,
        backgroundNotice: true, oldProxyIgnored: true, updateActions: actions, liveGitHubVersion: live?.latestVersion ?? 'not requested', browserErrors: 0 }));
} finally { await browser.close(); }

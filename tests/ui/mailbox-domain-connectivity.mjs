import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

// Run only against a disposable local test instance, with platform import disabled.
// MAILBOX_BASE_URL=http://127.0.0.1:PORT MAILBOX_TEST_ALLOW_WRITES=1 node tests/ui/mailbox-domain-connectivity.mjs
const baseUrl = process.env.MAILBOX_BASE_URL;
assert.equal(process.env.MAILBOX_TEST_ALLOW_WRITES, '1', 'Use a disposable app data directory for this test');
assert.match(baseUrl || '', /^http:\/\/127\.0\.0\.1:\d+$/);
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.MAILBOX_PLAYWRIGHT_MODULE || 'playwright');
const browser = await chromium.launch({ headless: true,
    ...(process.env.MAILBOX_BROWSER_EXECUTABLE ? { executablePath: process.env.MAILBOX_BROWSER_EXECUTABLE } : {}) });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const errors = [];
page.on('pageerror', error => errors.push(error.name));
let progress;
page.on('response', async response => {
    if (response.url().includes('CsvImportStatusJson')) {
        try { progress = await response.json(); } catch { /* Navigation may cancel a poll. */ }
    }
});
const files = [
    ['standard.csv', 'email,domain,credential,client_id\r\none@gmx.es,gmx.es,synthetic-code,synthetic-code\r\nduplicate@new-provider.example,new-provider.example,initial,\r\n'],
    ['chinese.csv', '\uFEFF邮箱,密码,IMAP授权码,2fa\r\nchinese@unknown.example,unused-password,imap-code,noise\r\n'],
    ['semicolon.csv', 'email;domain;credential;client_id\r\noauth@gmail.com;gmail.com;long-oauth-token;google-client\r\n'],
    ['tab.csv', '邮箱\t密码\r\ntab@custom.example\ttab-code\r\n'],
    ['no-header.csv', 'noheader@another.example,no-header-code\r\n'],
    ['stale-domain.csv', 'email,domain,credential,client_id\r\nstale@actual.example,wrong.example,stale-code,\r\n'],
    ['duplicate-and-invalid.csv', 'email,domain,credential,client_id\r\nduplicate@new-provider.example,new-provider.example,replacement,\r\ninvalid@unknown.example,unknown.example,,\r\n']
];
try {
    assert.equal((await page.goto(`${baseUrl}/MailAccounts/ImportCsv`)).status(), 200);
    await page.locator('input[type=file]').setInputFiles(files.map(([name, text]) => ({
        name, mimeType: 'text/csv', buffer: Buffer.from(text, 'utf8')
    })));
    await page.locator('#IsEnabled').uncheck();
    await page.locator('#saveBtn').click();
    const deadline = Date.now() + 15000;
    while (progress?.status !== 'AwaitingVerification' && Date.now() < deadline) await page.waitForTimeout(100);
    assert.equal(progress?.status, 'AwaitingVerification');
    assert.equal(progress.created + progress.updated, 7);
    assert.equal(progress.skipped, 1);
    assert.equal(progress.failed, 1);
    assert.equal(progress.warnings, 1);
    assert.equal(progress.formatWarnings, 0, 'Gmail OAuth must bypass app-password format warnings');
    assert.equal(progress.verificationStarted, false, 'Import must not connect to providers');

    await page.goto(`${baseUrl}/MailDomains?domain=new-provider.example`);
    await page.locator('#IncomingHost').fill('imap.manual-provider.example');
    await page.locator('#IncomingPort').fill('143');
    await page.locator('#IncomingImplicitTls').selectOption('false');
    await page.locator('#OutgoingHost').fill('smtp.manual-provider.example');
    await page.locator('#OutgoingPort').fill('465');
    await page.locator('#OutgoingImplicitTls').selectOption('true');
    await page.locator('summary').click();
    await page.locator('#OAuthTokenEndpoint').fill('https://login.manual-provider.example/token');
    await page.locator('#OAuthRequiresSecret').check();
    await page.locator('form button[type=submit]').click();
    await page.waitForSelector('.alert-success');
    assert.equal(await page.locator('#IncomingHost').inputValue(), 'imap.manual-provider.example');
    assert.equal(await page.locator('#IncomingImplicitTls').inputValue(), 'false');
    assert.equal(await page.locator('#OAuthTokenEndpoint').inputValue(), 'https://login.manual-provider.example/token');

    const withoutCsrf = await page.request.post(`${baseUrl}/MailDomains/Save`, { form: { Domain: 'evil.example' } });
    assert.equal(withoutCsrf.status(), 400, 'Domain configuration writes must require CSRF protection');
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ result: 'PASS', fileVariants: files.length, imported: 7, skipped: 1,
        invalidRows: 1, domainCorrectionWarnings: 1, importDoesNotConnect: true,
        manualImapStartTls: true, manualSmtpTls: true, customOAuthConfiguration: true, csrf: true }));
} finally { await browser.close(); }

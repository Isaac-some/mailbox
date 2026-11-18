import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href : 'playwright');
const base = process.env.MAILBOX_BASE_URL;
const current = process.env.MAILBOX_EXPECTED_VERSION || '2.4.4';
const published = process.env.MAILBOX_EXPECTED_PUBLISHED_VERSION;
assert.ok(base && published, 'Set the isolated app URL and expected GitHub version.');
const browser = await chromium.launch({ executablePath: '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome', headless: true });
try {
    const page = await browser.newPage();
    await page.goto(base + '/DesktopUpdates');
    await page.waitForFunction(() => {
        const text = document.querySelector('#update-message')?.textContent || '';
        return text.includes('本机版本高于 GitHub') || text.includes('当前版本与 GitHub') || text.includes('失败') || text.includes('超时');
    }, null, { timeout: 135000 });
    const state = await (await page.request.get(base + '/DesktopUpdates/Status')).json();
    assert.equal(state.currentVersion, current);
    assert.equal(state.latestVersion, published, JSON.stringify(state));
    assert.equal(state.phase, current === published ? 'current' : 'ahead');
    const label = await page.locator('#update-version').innerText();
    assert.ok(label.includes('当前版本：v' + current), label);
    assert.ok(label.includes('GitHub 已发布版本：v' + published), label);
    if (state.phase === 'ahead') {
        assert.ok(await page.locator('#update-notes-section').isHidden());
        assert.doesNotMatch(await page.locator('#update-message').innerText(), /已是最新/);
    }
    if (process.env.MAILBOX_SCREENSHOT) await page.screenshot({ path: process.env.MAILBOX_SCREENSHOT, fullPage: true });
    console.log(JSON.stringify({ result: 'PASS', current, published, phase: state.phase, realGitHub: true }));
} finally { await browser.close(); }

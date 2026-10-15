import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
const modulePath = process.env.PLAYWRIGHT_MODULE;
const { chromium } = await import(modulePath ? pathToFileURL(modulePath).href : 'playwright');
const browser = await chromium.launch({executablePath:'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',headless:true});
try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const started=Date.now();
    const base=process.env.MAILBOX_BASE_URL || 'http://127.0.0.1:5127';
    for(let attempt=0;attempt<60;attempt++) {
        try { const response=await page.request.get(base+'/MailAccounts',{timeout:2000});if(response.ok())break; }
        catch {}
        await page.waitForTimeout(1000);
    }
    await page.goto(base + '/MailAccounts');
    assert.equal(await page.locator('[data-initialization-status]').count(), 1,
        'First launch must expose initialization progress and automatically refresh account rows');
    await page.waitForFunction(() => document.querySelectorAll('.account-address').length >= 20, {timeout:60000});
    assert.equal(await page.locator('.account-address').count(),20,'The default page must contain twenty accounts');
    assert.deepEqual(errors, [], 'First launch must have no JavaScript errors');
    console.log(JSON.stringify({result:'PASS',checks:'automatic first page, initialization status, twenty accounts, no browser errors',readySeconds:(Date.now()-started)/1000}));
} finally {
    await browser.close();
}

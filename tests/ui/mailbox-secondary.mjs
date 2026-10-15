import assert from 'node:assert/strict';
import {readFileSync,writeFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const {chromium}=await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
const fixture=JSON.parse(readFileSync(process.env.MAILBOX_UI_FIXTURE,'utf8'));
const browser=await chromium.launch({executablePath:'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',headless:true});
const context=await browser.newContext({acceptDownloads:true});const page=await context.newPage();
const base='http://127.0.0.1:5131',checks=[],errors=[];
page.on('pageerror',e=>errors.push(e.message));
try {
    const details=base+'/MailAccounts/Details/'+fixture.dummyId;
    await page.goto(details);
    await page.locator('form[action*="ToggleEnabled"] button').click();
    await page.waitForURL('**/MailAccounts');
    await page.goto(details);
    assert.match(await page.locator('form[action*="ToggleEnabled"]').innerText(),/禁用/);
    await page.locator('form[action*="ToggleEnabled"] button').click();
    await page.waitForURL('**/MailAccounts');
    await page.goto(details);
    assert.match(await page.locator('form[action*="ToggleEnabled"]').innerText(),/启用/);
    checks.push('enable and disable a dedicated dummy account');
    await page.goto(base+'/MailAccounts/EditSyncTime/'+fixture.dummyId);
    await page.locator('#NewSyncTime').fill('2026-10-10T12:30');
    await page.locator('form button[type=submit]').click();
    await page.waitForURL(details);
    page.once('dialog',d=>d.accept());
    await page.locator('form[action*="ResetSyncTime"] button').click();
    await page.waitForTimeout(500);
    checks.push('edit and reset synchronization time on the dummy account');
    await page.goto(base+'/MailAccounts/ImportMBox');
    const subject='MAILBOX-LOCAL-IMPORT-'+Date.now();
    const raw=`From sender@example.com Thu Oct 15 12:00:00 2026\nFrom: sender@example.com\nTo: recipient@example.com\nSubject: ${subject}\nMessage-ID: <${subject}@example.com>\nDate: Thu, 15 Oct 2026 12:00:00 +0000\nMIME-Version: 1.0\nContent-Type: text/plain; charset=utf-8\n\nLocal import verification.\n\n`;
    await page.locator('#mboxFileInput').setInputFiles({name:'acceptance.mbox',mimeType:'application/mbox',buffer:Buffer.from(raw)});
    await page.locator('#accountSelect').selectOption(String(fixture.accountId));
    await page.locator('#folderSelect').selectOption('INBOX',{timeout:60000});
    await page.locator('#submitBtn').click();
    await page.waitForURL('**/MailAccounts/MBoxImportStatus?*');
    for(let i=0;i<30;i++) {
        if(/已完成/.test(await page.locator('main').innerText()))break;
        await page.waitForTimeout(1000);await page.reload();
    }
    assert.match(await page.locator('main').innerText(),/已完成/);
    checks.push('import a synthetic MBox into local storage and observe completion');
    await page.goto(base+'/MailAccounts/Export/'+fixture.accountId);
    await page.locator('input[name="Format"][value="EML"]').check();
    page.once('dialog',d=>d.accept());
    await page.locator('form button[type=submit]').click();
    await page.waitForURL('**/MailAccounts/ExportStatus?*');
    await page.locator('a[href*="DownloadExport"]').waitFor({state:'visible',timeout:60000});
    const [download]=await Promise.all([page.waitForEvent('download'),page.locator('a[href*="DownloadExport"]').click()]);
    assert.match(download.suggestedFilename(),/\.zip$/i);
    checks.push('export account as EML ZIP and download the actual archive');
    assert.deepEqual(errors,[]);
} catch(e) {checks.push('FAILED: '+e.message.split('\n')[0]);throw e;}
finally {writeFileSync(process.env.MAILBOX_UI_REPORT,JSON.stringify({checks,errors},null,2));await browser.close();console.log(JSON.stringify({checks,errors}));}

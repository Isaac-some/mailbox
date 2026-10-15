import assert from 'node:assert/strict';
import {readFileSync,writeFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const {chromium}=await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
const base=process.env.MAILBOX_BASE_URL||'http://127.0.0.1:5131';
const fixture=JSON.parse(readFileSync(process.env.MAILBOX_UI_FIXTURE,'utf8'));
const browser=await chromium.launch({executablePath:'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',headless:true});
const context=await browser.newContext({acceptDownloads:true,viewport:{width:1440,height:1000}});
const page=await context.newPage();
const checks=[];const errors=[];
page.on('pageerror',e=>errors.push(e.message));
const suffix=Date.now().toString(36),dummy=`acceptance-ui-${suffix}@yahoo.com`;
try {
    await page.goto(base+'/MailAccounts/Create');
    await page.locator('#EmailAddress').fill(dummy);
    await page.locator('#EmailAddress').blur();
    await page.locator('#PasswordInput').fill('dummy-test-only-code');
    await page.locator('#IsEnabled').uncheck();
    await page.locator('#saveBtn').click();
    await page.waitForURL('**/MailAccounts');
    await page.goto(base+'/MailAccounts?q='+encodeURIComponent(dummy));
    assert.equal(await page.locator('.account-address').count(),1);
    const dummyId=await page.locator('[data-account-id]').first().getAttribute('data-account-id');
    checks.push('create disabled test account and find it');

    await page.goto(base+'/MailAccounts/Edit/'+dummyId);
    assert.equal(await page.locator('#PasswordInput').inputValue(),'','Saved password must not be sent back to the form');
    await page.locator('[data-target="PasswordInput"]').click();
    assert.equal(await page.locator('#PasswordInput').getAttribute('type'),'text');
    await page.locator('[data-target="PasswordInput"]').click();
    assert.equal(await page.locator('#PasswordInput').getAttribute('type'),'password');
    await page.locator('#saveBtn').click();
    await page.waitForURL('**/MailAccounts');
    assert.ok(!page.url().includes('/Edit/'),'Blank password retains current credentials and saves');
    checks.push('edit, show/hide password, retain blank password, save');

    await page.goto(base+`/Emails/Details/${fixture.emailId}`);
    const [eml]=await Promise.all([page.waitForEvent('download'),
        page.locator('a[href*="/Emails/Export/"]:visible').click()]);
    assert.match(eml.suggestedFilename(),/\.eml$/i);
    const attachment=page.locator('.attachment-preview-btn').filter({visible:true}).first();
    await attachment.click();
    await page.locator('#attachmentPreviewModal').waitFor({state:'visible'});
    const [single]=await Promise.all([page.waitForEvent('download'),page.locator('#downloadAttachmentBtn').click()]);
    assert.equal(single.suggestedFilename(),'acceptance.txt');
    await page.locator('#attachmentPreviewModal [data-bs-dismiss="modal"]').first().click();
    const [all]=await Promise.all([page.waitForEvent('download'),page.locator('a[href*="DownloadAllAttachments"]').click()]);
    assert.match(all.suggestedFilename(),/\.zip$/i);
    const [full]=await Promise.all([context.waitForEvent('page'),page.locator('#full-view-link').click()]);
    await full.waitForLoadState('domcontentloaded');
    assert.equal((await full.content()).includes('MAILBOX-ACCEPT-20261015'),true);await full.close();
    checks.push('email export, attachment preview, single and ZIP download, full view');

    await page.goto(base+'/OutboundMailTasks');
    await page.locator('#senderSourceText').fill(fixture.senderEmail);
    await page.locator('#matchSenderButton').click();
    await page.waitForFunction(()=>document.querySelector('#selectedSenderCount').textContent==='1');
    const [selected]=await Promise.all([page.waitForEvent('download'),page.locator('#downloadSenderCsv').click()]);
    assert.match(selected.suggestedFilename(),/\.csv$/i);
    checks.push('match pasted sender list and download selected CSV');
    await page.locator('#timingModeScheduled').check();
    const csv='发件邮箱,收件邮箱,主题,正文,时间\n'+`${fixture.senderEmail},${fixture.senderEmail},UI-validation-${suffix},scheduled test only,2050-01-01 10:00:00\n`;
    await page.locator('#CsvFile').setInputFiles({name:'UI-validation.csv',mimeType:'text/csv',buffer:Buffer.from(csv)});
    await page.locator('#createTaskButton').click();
    await page.waitForURL('**/OutboundMailTasks/Details/*');
    assert.match(await page.locator('main').innerText(),/待发送/);
    page.once('dialog',dialog=>dialog.accept());
    await page.getByRole('button',{name:'取消待发邮件',exact:true}).click();
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(300);
    assert.match(await page.locator('main').innerText(),/已取消/);
    checks.push('scheduled task CSV creation and cancellation without sending');

    await page.goto(base+'/MailAccounts/ImportCsv');
    const accountCsv=`邮箱,IMAP授权码\nacceptance-csv-${suffix}@gmx.com,dummy-test-only-code\ninvalid-address,dummy-test-only-code\n`;
    await page.locator('#accountFileInput').setInputFiles({name:'UI-accounts.csv',mimeType:'text/csv',buffer:Buffer.from(accountCsv)});
    await page.locator('#saveBtn').click();
    await page.waitForLoadState('domcontentloaded');
    await page.locator('#csvImportTasks').waitFor({state:'visible'});
    await page.waitForFunction(()=>/等待验证|已完成|失败/.test(document.querySelector('#csvImportTasksList').textContent),{timeout:30000});
    await page.locator('#csvImportTasksToggle').click();
    assert.equal(await page.locator('#csvImportTasksToggle').getAttribute('aria-expanded'),'false');
    await page.locator('#csvImportTasksToggle').click();
    assert.equal(await page.locator('#csvImportTasksToggle').getAttribute('aria-expanded'),'true');
    checks.push('mixed valid/invalid account CSV, actual progress, task collapse and expand');

    await page.goto(base+'/MailAccounts?q='+encodeURIComponent(dummy));
    await page.getByRole('button',{name:'删除账号',exact:true}).click();
    assert.match(await page.locator('#deleteAccountMessage').innerText(),new RegExp(dummy.replaceAll('.','\\.')));
    await page.locator('#confirmSingleDelete').click();
    await page.waitForLoadState('domcontentloaded');
    await page.goto(base+'/MailAccounts?q='+encodeURIComponent(dummy));
    await page.waitForTimeout(600);
    assert.equal(await page.locator('.account-address').count(),0);
    checks.push('single-account delete confirms the target and removes it');

    await page.goto(base+'/LocalMaintenance');
    page.once('dialog',dialog=>dialog.dismiss());
    await page.getByRole('button',{name:'清空邮件和附件',exact:true}).click();
    assert.equal(await page.getByRole('button',{name:'清空邮件和附件',exact:true}).isEnabled(),true,'Cancel must not disable the button');
    page.once('dialog',dialog=>dialog.accept());
    await page.getByRole('button',{name:'清空邮件和附件',exact:true}).click();
    await page.waitForLoadState('domcontentloaded');
    assert.match(await page.locator('main').innerText(),/邮件和附件已清空，邮箱账号已保留/);
    checks.push('clear-mail cancel and confirm; accounts retained');
    assert.deepEqual(errors,[]);
} catch(error) {
    checks.push('FAILED: '+error.message.split('\n')[0]);throw error;
} finally {
    writeFileSync(process.env.MAILBOX_UI_REPORT,JSON.stringify({checks,errors},null,2));
    await browser.close();console.log(JSON.stringify({checks:checks.length,errors:errors.length,last:checks.at(-1)}));
}

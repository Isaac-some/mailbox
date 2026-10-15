import assert from 'node:assert/strict';
import {readFileSync,writeFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const {chromium} = await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
const base = process.env.MAILBOX_BASE_URL || 'http://127.0.0.1:5131';
const fixture = JSON.parse(readFileSync(process.env.MAILBOX_UI_FIXTURE,'utf8'));
const browser = await chromium.launch({executablePath:'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',headless:true});
const report = {pages:[],checks:[],errors:[]};
const context = await browser.newContext({viewport:{width:1440,height:1000},acceptDownloads:true});
const page = await context.newPage();
page.on('pageerror',e => report.errors.push({path:new URL(page.url()).pathname,error:e.message}));
const routes = ['/MailAccounts','/MailAccounts/Create','/MailAccounts/ImportCsv','/OutboundMail','/OutboundMailTasks',
    '/LocalMaintenance','/LocalMaintenance/Diagnostics','/Emails/Jobs',
    `/MailAccounts/Details/${fixture.accountId}`,`/MailAccounts/Edit/${fixture.accountId}`,
    `/MailAccounts/Export/${fixture.accountId}`,`/Emails?SelectedAccountId=${fixture.accountId}`,
    `/Emails/Details/${fixture.emailId}`];
try {
    for (const route of routes) {
        const start=Date.now();
        const response=await page.goto(base+route,{waitUntil:'domcontentloaded',timeout:60000});
        await page.waitForTimeout(200);
        const controls=await page.locator('button,input[type=submit],a.btn').evaluateAll(nodes=>nodes.map(n=>({
            name:(n.innerText||n.value||n.getAttribute('aria-label')||n.title||'').trim().replace(/\s+/g,' '),
            disabled:n.disabled||false,visible:!!(n.offsetWidth||n.offsetHeight||n.getClientRects().length)
        })));
        report.pages.push({route,status:response.status(),milliseconds:Date.now()-start,controls});
        assert.equal(response.status(),200,route+' should load');
    }
    await page.goto(base+'/MailAccounts');
    await page.getByRole('button',{name:'搜索',exact:true}).click();
    await page.waitForLoadState('domcontentloaded');
    assert.equal(await page.locator('.account-address').count(),20);
    report.checks.push('account search and twenty-row pagination');
    await page.getByRole('link',{name:'下一页',exact:true}).click();
    await page.waitForLoadState('domcontentloaded');
    assert.equal(new URL(page.url()).searchParams.get('page'),'2');
    await page.getByRole('link',{name:'上一页',exact:true}).click();
    report.checks.push('next and previous page');

    await page.goto(base+'/MailAccounts/Create');
    for (const [address,label] of [['audit@gmail.com','Gmail'],['audit@yahoo.com','Yahoo'],['audit@gmx.com','GMX'],['audit@outlook.com','Outlook']]) {
        await page.locator('#EmailAddress').fill(address);
        await page.locator('#EmailAddress').blur();
        assert.match(await page.locator('#providerDetection').innerText(),new RegExp(label,'i'));
    }
    const password=page.locator('#PasswordInput');
    if(await password.isVisible()) {
        await page.locator('[data-target="PasswordInput"]').click();
        assert.equal(await password.getAttribute('type'),'text');
        await page.locator('[data-target="PasswordInput"]').click();
        assert.equal(await password.getAttribute('type'),'password');
    }
    report.checks.push('provider detection and password visibility');

    await page.goto(base+'/OutboundMail?accountId='+fixture.accountId);
    await page.locator('#To').fill('not-an-address');
    await page.locator('#Subject').fill('Validation only');
    await page.locator('#Body').fill('Do not send');
    await page.locator('#sendButton').click();
    await page.waitForTimeout(300);
    assert.equal(await page.locator('#sendButton').isEnabled(),true,'Invalid form must leave Send available');
    report.checks.push('invalid recipient validation preserves Send');
    const token = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
    const blank = await context.request.post(base+'/OutboundMail',{form:{
        AccountId:String(fixture.accountId),To:'',Subject:'',Body:'',__RequestVerificationToken:token
    }});
    assert.equal(blank.status(),200,'Empty fields must return validation, never an error page');
    const blankHtml = (await blank.text()).replace(/&#x([0-9a-f]+);/gi,(_,hex)=>String.fromCodePoint(parseInt(hex,16)));
    assert.match(blankHtml,/请填写收件人|请填写主题|请填写正文/);
    report.checks.push('empty compose server validation');

    await page.goto(base+`/Emails/Details/${fixture.emailId}`);
    await page.locator('#replyButton').click();
    await page.waitForURL('**/OutboundMail?replyToEmailId=*');
    assert.ok((await page.locator('#To').inputValue()).includes('@'));
    assert.match(await page.locator('#Subject').inputValue(),/^Re:/i);
    assert.match(await page.locator('#Body').inputValue(),/原邮件/);
    report.checks.push('reply opens in-app composer with recipient and original mail');

    await page.goto(base+'/LocalMaintenance');
    await page.locator('#network-mode').selectOption('ExplicitProxy');
    assert.equal(await page.locator('#proxy-host').isVisible(),true);
    assert.equal(await page.locator('#proxy-host').getAttribute('required')!==null,true);
    await page.locator('#network-mode').selectOption('System');
    assert.equal(await page.locator('#proxy-host').isVisible(),false);
    await page.getByRole('button',{name:'保存网络策略'}).click();
    await page.waitForLoadState('domcontentloaded');
    assert.match(await page.locator('main').innerText(),/网络策略已保存/);
    report.checks.push('proxy mode, required fields, save');

    await page.goto(base+'/LocalMaintenance/Diagnostics');
    await page.locator('#diagnostic-target').fill('not-a-url');
    await page.getByRole('button',{name:'开始检测'}).click();
    await page.waitForLoadState('domcontentloaded');
    assert.match(await page.locator('main').innerText(),/请输入有效的 HTTPS 地址/);
    report.checks.push('diagnostic invalid address explains recovery');

    await page.goto(base+'/OutboundMailTasks');
    await page.locator('#selectVisibleSenders').click();
    assert.ok(Number(await page.locator('#selectedSenderCount').innerText())>0);
    await page.locator('#clearSenderSelection').click();
    assert.equal(await page.locator('#selectedSenderCount').innerText(),'0');
    assert.equal(await page.locator('#downloadSenderCsv').isDisabled(),true);
    const download=page.waitForEvent('download');
    await page.getByRole('link',{name:'下载模板',exact:true}).click();
    assert.match((await download).suggestedFilename(),/\.csv$/i);
    report.checks.push('sender selection, clear, disabled empty download, CSV template');

    await page.goto(base+'/MailAccounts');
    await page.getByRole('button',{name:'按保留名单清理',exact:true}).click();
    await page.locator('#whitelistSourceText').fill('no-valid-address');
    await page.locator('#previewWhitelistDeletion').click();
    await page.waitForTimeout(300);
    assert.equal(await page.locator('#confirmWhitelistDeletion').isDisabled(),true);
    await page.getByRole('button',{name:'取消',exact:true}).filter({visible:true}).first().click();
    report.checks.push('whitelist empty preview cannot authorize deletion');
    assert.deepEqual(report.errors,[],'User-facing pages must have no JavaScript errors');
} catch(error) {
    report.failure=error.message;
    throw error;
} finally {
    writeFileSync(process.env.MAILBOX_UI_REPORT,JSON.stringify(report,null,2));
    await browser.close();
    console.log(JSON.stringify({pages:report.pages.length,checks:report.checks.length,errors:report.errors.length,failure:report.failure||null}));
}

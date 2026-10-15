import assert from 'node:assert/strict';
import {readFileSync,writeFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const {chromium}=await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
const root=process.env.MAILBOX_NATIVE_DATA;
assert.ok(root?.includes('/.runtime/acceptance-20261015/'),'Reset is limited to this explicit isolated test directory');
const oldKey=readFileSync(root+'/credential-encryption.key','utf8');
const browser=await chromium.launch({executablePath:'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',headless:true});
const page=await browser.newPage();
const base='http://127.0.0.1:5133';
try {
    await page.goto(base+'/LocalMaintenance');
    page.once('dialog',dialog=>dialog.dismiss());
    await page.locator('form[action$="FactoryReset"] button').click();
    assert.equal(readFileSync(root+'/credential-encryption.key','utf8'),oldKey);
    page.once('dialog',dialog=>dialog.accept());
    const started=Date.now();
    await page.locator('form[action$="FactoryReset"] button').click();
    let reset=false,ready=false;
    for(let attempt=0;attempt<120;attempt++) {
        try {
            reset=readFileSync(root+'/credential-encryption.key','utf8')!==oldKey;
            const response=await page.request.get(base+'/MailAccounts/InitializationStatus',{timeout:2000});
            const status=await response.json();
            if(reset && status.loaded>=20) {ready=true;break;}
        } catch {}
        await page.waitForTimeout(1000);
    }
    assert.ok(reset,'Factory reset must replace the encryption key');
    assert.ok(ready,'Native wrapper must restart and automatically refill the first page');
    await page.goto(base+'/MailAccounts');
    assert.equal(await page.locator('.account-address').count(),20);
    const report={result:'PASS',cancelPreservedData:true,keyRegenerated:true,automaticRestart:true,firstPageCount:20,resetToReadySeconds:(Date.now()-started)/1000};
    writeFileSync(process.env.MAILBOX_UI_REPORT,JSON.stringify(report,null,2));
    console.log(JSON.stringify(report));
} finally {await browser.close();}

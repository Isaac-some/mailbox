import {writeFileSync} from 'node:fs';
import {pathToFileURL} from 'node:url';
const {chromium}=await import(pathToFileURL(process.env.PLAYWRIGHT_MODULE).href);
const {default:AxeBuilder}=await import(pathToFileURL(process.env.AXE_MODULE).href);
const browser=await chromium.launch({executablePath:'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',headless:true});
const context=await browser.newContext({viewport:{width:1440,height:1000}});
const page=await context.newPage();
const results=[];
try {
    for(const route of ['/MailAccounts','/MailAccounts/Create','/OutboundMail','/LocalMaintenance','/OutboundMailTasks']) {
        await page.goto('http://127.0.0.1:5131'+route,{waitUntil:'domcontentloaded'});
        const audit=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21aa']).analyze();
        results.push({route,violations:audit.violations.map(v=>({id:v.id,impact:v.impact,description:v.description,
            nodes:v.nodes.slice(0,5).map(n=>({target:n.target,summary:n.failureSummary}))}))});
    }
    await page.goto('http://127.0.0.1:5131/MailAccounts');
    await page.waitForTimeout(300);
    await page.evaluate(()=>{
        document.querySelectorAll('.account-address').forEach((n,i)=>n.textContent=`sample${i+1}@example.com`);
        document.querySelectorAll('.account-name').forEach(n=>n.textContent='示例邮箱');
    });
    await page.screenshot({path:process.env.MAILBOX_SCREENSHOT});
    await page.setViewportSize({width:390,height:844});
    results.push({route:'narrow-window',documentWidth:await page.evaluate(()=>document.documentElement.scrollWidth),viewport:390});
} finally {
    writeFileSync(process.env.MAILBOX_UI_REPORT,JSON.stringify(results,null,2));
    await browser.close();
    console.log(JSON.stringify(results.map(r=>({route:r.route,violations:r.violations?.length,width:r.documentWidth}))));
}

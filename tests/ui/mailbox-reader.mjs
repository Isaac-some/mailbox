import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// Execute the actual reader script with deterministic responses: no real mailbox
// or browser timezone is needed to reproduce the missing failure notification.
const source = readFileSync(process.env.MAILBOX_RENDERED_PAGE
    || new URL('../../Views/Emails/Index.cshtml', import.meta.url), 'utf8');
const script = [...source.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)]
    .map(match => match[1]).find(code => code.includes("document.querySelector('[data-mailbox-sync-account]')"));
assert.ok(script,'the real mailbox reader script must be present');
async function scenario(startResponse, finalJob) {
    const error = { hidden: true, textContent: '' };
    const status = { hidden: true };
    const button = { disabled: false };
    let submit, poll, reloads = 0, calls = 0;
    const form = {
        action: '/sync',
        addEventListener: (_, callback) => { submit = callback; },
        querySelector: () => button
    };
    const document = {
        querySelectorAll: selector => selector === '[data-mailbox-sync-form]' ? [form] : [],
        querySelector: selector => selector.includes('sync-account') ? {dataset:{mailboxSyncAccount:'1'}}
            : selector.includes('RequestVerificationToken') ? {value:'test-token'} : button,
        getElementById: id => ({
            'mailbox-sync-error': error, 'mailbox-sync-status': status,
            'mailbox-sync-message': {textContent:''}, 'mailbox-refresh-form': form
        })[id]
    };
    vm.runInNewContext(script, {
        document, URLSearchParams, URL,
        matchMedia: () => ({matches:false}),
        FormData: class { *[Symbol.iterator]() { yield ['__RequestVerificationToken','test-token']; } },
        window: {
            location: {search:'',href:'http://localhost/',reload:() => reloads++},
            matchMedia: () => ({matches:false}),
            setInterval: callback => { poll=callback; },setTimeout:()=>{}
        },
        fetch: async url => {
            calls++;
            return url === '/sync' ? startResponse : {ok:true,json:async()=>({state:'NotQueued',job:finalJob})};
        }
    });
    assert.equal(calls,0,'reading cached email must not start a sync');
    await submit({preventDefault(){},currentTarget:form});
    await poll();
    await poll();
    return {error,reloads};
}

const accepted = {ok:true,json:async()=>({state:'Queued',requestedAt:'2026-09-03T00:00:00Z'})};
const failed = await scenario(accepted,{status:'Failed',errorCode:'AuthenticationRejected',message:'授权码无效，请检查邮箱与授权码是否匹配。'});
assert.equal(failed.reloads,0,'failed sync must not silently reload away the error');
assert.equal(failed.error.hidden,false);
assert.match(failed.error.textContent,/授权码无效/);

const upstream = await scenario({ok:false,json:async()=>({message:'线上账号接口不可用'})},null);
assert.equal(upstream.error.hidden,false);
assert.match(upstream.error.textContent,/线上账号接口不可用/);

const completed = await scenario(accepted,{status:'Completed'});
assert.equal(completed.reloads,1);
console.log('PASS: connection failure stays visible, upstream failure is explained, successful sync reloads once');

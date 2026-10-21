(() => {
    document.querySelectorAll('[data-sender-picker]').forEach(root => {
        const selected = new Map(), states = new Map(), pending = new Set();
        const search = root.querySelector('[data-search]') || document.getElementById(root.dataset.externalSearch);
        const results = root.querySelector('[data-results]'), chips = root.querySelector('[data-selected]');
        const preparation = root.querySelector('[data-preparation]');
        const max = Number(root.dataset.max), inputName = root.dataset.inputName;
        const compose = inputName === 'AccountIds';
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
        let generation = 0, current = [], page = 1, timer, polling = false, preparing = false;
        if (compose) results.hidden = true;
        const button = (label, action, className = 'btn btn-light btn-sm') => {
            const element = document.createElement('button'); element.type = 'button'; element.className = className;
            element.textContent = label; element.addEventListener('click', action); return element;
        };
        async function post(action, ids) {
            const data = new URLSearchParams(); data.set('__RequestVerificationToken', token);
            ids.forEach(id => data.append('SelectedAccountIds', id));
            const response = await fetch('/OutboundMailTasks/' + action, {method:'POST', body:data});
            if (!response.ok) throw new Error('授权更新未完成，请重试。');
            return response.json();
        }
        function render() {
            chips.replaceChildren();
            [...selected.values()].slice(0, 30).forEach(account => {
                const chip = document.createElement('span'); chip.className = 'sender-chip';
                const text = document.createElement('span'); text.textContent = account.emailAddress;
                const remove = button('×', () => { selected.delete(account.id); states.delete(account.id); pending.delete(account.id); render(); });
                remove.setAttribute('aria-label', '移除 ' + account.emailAddress);
                const input = document.createElement('input'); input.type = 'hidden'; input.name = inputName; input.value = account.id;
                chip.append(text, remove, input); chips.append(chip);
            });
            if (selected.size > 30) {
                const more = document.createElement('span');
                more.textContent = `另有 ${selected.size - 30} 个已选账号，可搜索后单独移除。`;
                chips.append(more);
            }
            const queued = [...states.values()].filter(s => s.state === 'Queued' || s.state === 'Running').length + pending.size;
            const ready = [...states.values()].filter(s => s.state === 'NotQueued' && s.canSend).length;
            const failed = [...states.values()].filter(s => s.state === 'NotQueued' && !s.canSend);
            preparation.replaceChildren(document.createTextNode(selected.size ? `已选 ${selected.size} 个 · 更新中 ${queued} 个 · 可发件 ${ready} 个` : ''));
            if (failed.length) {
                const details = document.createElement('span');
                details.textContent = ` · ${failed.length} 个未通过：${failed.slice(0, 3).map(a => a.emailAddress).join('、')}`;
                preparation.append(details, button('重试未通过账号', () => { failed.forEach(a => pending.add(a.id)); flush(); }));
            }
            results.querySelectorAll('[data-account-id]').forEach(row => {
                const checked = selected.has(Number(row.dataset.accountId));
                row.setAttribute('aria-pressed', String(checked));
                row.classList.toggle('sender-account-row--selected', checked);
            });
            root.dispatchEvent(new CustomEvent('selectionchange', {detail:[...selected.values()]}));
        }
        async function poll() {
            if (polling || !selected.size) return; polling = true;
            try {
                const data = await post('SenderStatus', [...selected.keys()]);
                data.accounts.forEach(a => { if (selected.has(a.id)) states.set(a.id, a); }); render();
            } catch(e) { preparation.textContent = e.message; }
            finally { polling = false; }
        }
        async function flush() {
            if (preparing || !pending.size) return;
            preparing = true; const ids = [...pending]; ids.forEach(id => pending.delete(id));
            ids.forEach(id => states.set(id, {...selected.get(id), state:'Queued'})); render();
            try { await post('PrepareSenders', ids); await poll(); }
            catch(e) {
                ids.forEach(id => states.set(id, {...selected.get(id), state:'NotQueued', canSend:false})); render();
                preparation.append(document.createTextNode(' · ' + e.message));
            } finally { preparing = false; if (pending.size) flush(); }
        }
        function add(account, renderNow = true) {
            if (!account.isEnabled || selected.has(account.id)) return;
            if (selected.size >= max) { preparation.textContent = `最多选择 ${max} 个账号。`; return; }
            selected.set(account.id, account); pending.add(account.id); if (renderNow) render();
        }
        async function load(append = false) {
            const request = ++generation;
            if (!append) { page = 1; results.textContent = '正在搜索…'; }
            try {
                const response = await fetch('/OutboundMailTasks/SearchSenders?' + new URLSearchParams({query:search.value, page}));
                if (!response.ok) throw new Error('搜索失败，请重试。');
                const data = await response.json(); if (request !== generation) return;
                if (!append) { results.replaceChildren(); current = []; }
                results.querySelector('[data-more]')?.remove(); current.push(...data.accounts);
                data.accounts.forEach(account => {
                    const row = button(account.emailAddress + (account.isEnabled ? (account.canSend ? ' · 可发件' : ' · 选择后验证') : ' · 已停用'), () => {
                        if (selected.has(account.id)) { selected.delete(account.id); states.delete(account.id); pending.delete(account.id); render(); }
                        else { add(account); flush(); }
                        if (compose) { search.value = ''; results.hidden = true; }
                    }, 'sender-account-row btn text-start w-100');
                    row.disabled = !account.isEnabled; row.dataset.accountId = account.id;
                    row.setAttribute('aria-pressed', String(selected.has(account.id)));
                    row.classList.toggle('sender-account-row--selected', selected.has(account.id));
                    results.append(row);
                });
                if (!current.length) results.textContent = '没有找到账号，请确认该邮箱已导入。';
                if (data.hasMore) { const more = button('加载更多', () => { page++; load(true); }); more.dataset.more = '1'; results.append(more); }
            } catch(e) { if (request === generation) results.textContent = e.message; }
        }
        search.addEventListener('focus', () => { results.hidden = false; });
        search.addEventListener('input', () => { results.hidden = false; clearTimeout(timer); generation++; timer = setTimeout(() => load(), 200); });
        if (compose) document.addEventListener('click', event => { if (!root.contains(event.target)) results.hidden = true; });
        search.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); clearTimeout(timer); load(); } });
        root.senderPicker = {
            clear() { selected.clear(); states.clear(); pending.clear(); render(); },
            selectVisible() { current.forEach(account => add(account, false)); render(); flush(); },
            replace(accounts) { selected.clear(); states.clear(); pending.clear(); accounts.forEach(account => add(account, false)); render(); flush(); }
        };
        try { JSON.parse(root.dataset.initial || '[]').forEach(account => add(account, false)); } catch {}
        render(); load(); flush();
        setInterval(() => { if ([...states.values()].some(s => s.state !== 'NotQueued')) poll(); }, 1500);
    });
})();

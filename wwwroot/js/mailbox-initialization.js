(() => {
    const notice = document.querySelector('[data-initialization-status]');
    if (!notice) return;
    let loaded = Number(notice.dataset.loaded || 0);
    let stopped = false;
    window.addEventListener('pagehide', () => { stopped = true; });
    document.querySelector('.account-search')?.addEventListener('submit', () => { stopped = true; });
    async function poll() {
        try {
            const response = await fetch(notice.dataset.statusUrl, {cache: 'no-store'});
            if (!response.ok) throw new Error('暂时无法获取初始化进度。');
            const status = await response.json();
            if (!status.enabled) { notice.hidden = true; return; }
            notice.textContent = status.message;
            notice.className = status.error ? 'alert alert-warning' : 'alert alert-info';
            const incomplete = document.querySelectorAll('.account-address').length < 20;
            if (status.loaded > loaded && (incomplete || !status.running) && !notice.dataset.search && !stopped) {
                // Replace only the rows. Whole-page reloads erase search input
                // and interrupt the user's current operation.
                const url = new URL(window.location.href);
                url.searchParams.delete('priorityLookup');
                const page = await fetch(url, {cache: 'no-store'});
                if (!page.ok) throw new Error('邮箱列表暂未更新，请稍后重试。');
                const html = new DOMParser().parseFromString(await page.text(), 'text/html');
                const current = document.querySelector('.account-table-wrap');
                const incoming = html.querySelector('.account-table-wrap');
                // Fresh rows need the existing page's event handlers. Notify
                // the account page to bind just the new sync forms.
                if (current && incoming && !stopped) {
                    if (incomplete) {
                        current.replaceWith(incoming);
                        document.dispatchEvent(new Event('mailbox:accounts-updated'));
                    }
                    const navigation = document.querySelector('.account-pagination');
                    const nextNavigation = html.querySelector('.account-pagination');
                    if (navigation && nextNavigation) navigation.replaceWith(nextNavigation);
                    else if (nextNavigation) document.querySelector('.account-table-wrap').after(nextNavigation);
                    loaded = status.loaded;
                }
            }
            if (status.running && !stopped) window.setTimeout(poll, 1000);
        } catch (error) {
            notice.textContent = '正在重新连接以获取初始化进度；可继续搜索邮箱。';
            if (!stopped) window.setTimeout(poll, 3000);
        }
    }
    poll();
})();

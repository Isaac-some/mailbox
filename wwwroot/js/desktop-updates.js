(() => {
    'use strict';
    const byId = id => document.getElementById(id);
    const check = byId('update-check'), download = byId('update-download'), install = byId('update-install');
    const message = byId('update-message'), progress = byId('update-progress');
    let polling = false, installing = false;
    function render(state) {
        installing = state.phase === 'installing';
        byId('update-version').textContent = `当前版本：v${state.currentVersion}` +
            (state.latestVersion ? `　最新版本：v${state.latestVersion}` : '');
        message.textContent = state.message || '点击检查更新以获取最新版本。';
        message.className = `alert alert-${state.phase === 'error' ? 'warning' : 'info'}`;
        const busy = ['checking', 'downloading', 'preparing', 'installing'].includes(state.phase);
        check.disabled = busy;
        check.hidden = ['ready', 'installing'].includes(state.phase);
        download.hidden = !['available', 'error'].includes(state.phase) || !state.latestVersion;
        download.disabled = busy;
        install.hidden = state.phase !== 'ready';
        install.disabled = busy;
        progress.hidden = state.phase !== 'downloading';
        progress.max = state.total || 1;
        progress.value = state.downloaded;
        if (!progress.hidden) message.textContent += ` ${Math.floor(100 * state.downloaded / (state.total || 1))}%`;
        byId('update-notes-section').hidden = !state.notes;
        byId('update-notes').textContent = state.notes || '';
    }
    async function status() {
        try {
            const response = await fetch('/DesktopUpdates/Status', { cache: 'no-store' });
            if (!response.ok) throw new Error('更新状态读取失败，请重试。');
            render(await response.json());
        } catch (error) {
            if (installing) message.textContent = '正在重新打开邮箱助手，完成后请在新窗口继续使用。';
            else message.textContent = error.message;
        }
    }
    async function action(name) {
        check.disabled = download.disabled = install.disabled = true;
        try {
            const token = document.querySelector('#update-actions input[name="__RequestVerificationToken"]').value;
            const response = await fetch(`/DesktopUpdates/${name}`, {
                method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: new URLSearchParams({ __RequestVerificationToken: token })
            });
            if (!response.ok) throw new Error('更新操作未完成，请重试。');
            render(await response.json());
        } catch (error) { message.textContent = error.message; check.disabled = download.disabled = install.disabled = false; }
    }
    check.addEventListener('click', () => action('Check'));
    download.addEventListener('click', () => action('Download'));
    install.addEventListener('click', () => action('Install'));
    status();
    setInterval(async () => {
        if (polling || document.hidden) return;
        polling = true;
        try { await status(); } finally { polling = false; }
    }, 1000);
})();

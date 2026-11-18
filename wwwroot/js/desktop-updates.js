(() => {
    'use strict';
    const byId = id => document.getElementById(id);
    const check = byId('update-check'), download = byId('update-download'), install = byId('update-install');
    const message = byId('update-message'), progress = byId('update-progress');
    let polling = false, installing = false, actionInFlight = false, automatic = true;
    let lastInstallAttempt = 0;
    function render(state) {
        installing = state.phase === 'installing';
        byId('update-version').textContent = `当前版本：v${state.currentVersion}` +
            (state.latestVersion ? `　GitHub 已发布版本：v${state.latestVersion}` : '');
        message.textContent = state.message || '点击检查更新以获取最新版本。';
        message.className = `alert alert-${state.phase === 'error' ? 'warning' : 'info'}`;
        const busy = ['checking', 'downloading', 'preparing', 'installing'].includes(state.phase);
        check.disabled = busy;
        check.hidden = ['ready', 'installing'].includes(state.phase);
        download.hidden = automatic || !['available', 'error'].includes(state.phase) || !state.latestVersion;
        download.disabled = busy;
        install.hidden = automatic || state.phase !== 'ready';
        install.disabled = busy;
        progress.hidden = state.phase !== 'downloading';
        progress.max = state.total || 1;
        progress.value = state.downloaded;
        if (!progress.hidden) message.textContent += ` ${Math.floor(100 * state.downloaded / (state.total || 1))}%`;
        byId('update-notes-section').hidden = !state.notes;
        byId('update-notes').textContent = state.notes || '';
    }
    async function advance(state) {
        if (!automatic || actionInFlight) return;
        if (state.phase === 'available') await action('Download');
        else if (state.phase === 'ready' && Date.now() - lastInstallAttempt >= 5000) await action('Install');
    }
    function accept(state) {
        if (['current', 'ahead', 'error'].includes(state.phase)) automatic = false;
        render(state);
    }
    async function status() {
        if (actionInFlight) return;
        try {
            const response = await fetch('/DesktopUpdates/Status', { cache: 'no-store' });
            if (!response.ok) throw new Error('更新状态读取失败，请重试。');
            const state = await response.json();
            accept(state);
            await advance(state);
        } catch (error) {
            if (installing) message.textContent = '正在重新打开邮箱助手，完成后请在新窗口继续使用。';
            else message.textContent = error.message;
        }
    }
    async function action(name) {
        if (actionInFlight) return;
        actionInFlight = true;
        if (name === 'Install') lastInstallAttempt = Date.now();
        check.disabled = download.disabled = install.disabled = true;
        let state;
        try {
            const token = document.querySelector('#update-actions input[name="__RequestVerificationToken"]').value;
            const response = await fetch(`/DesktopUpdates/${name}`, {
                method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: new URLSearchParams({ __RequestVerificationToken: token })
            });
            if (!response.ok) throw new Error('更新操作未完成，请重试。');
            state = await response.json();
            if (name === 'Install' && state.phase === 'ready' && !state.waitingForWork) automatic = false;
            accept(state);
        } catch (error) {
            automatic = false;
            message.textContent = error.message;
            check.disabled = download.disabled = install.disabled = false;
            check.hidden = false;
        } finally { actionInFlight = false; }
        // A busy import/send can defer installation. Polling retries after five
        // seconds; never recursively retry the same ready state here.
        if (state && name !== 'Install') await advance(state);
    }
    check.addEventListener('click', () => { automatic = true; return action('Check'); });
    download.addEventListener('click', () => { automatic = true; return action('Download'); });
    install.addEventListener('click', () => { automatic = true; return action('Install'); });
    // Opening “检查更新” in the native menu expresses the user's update intent.
    action('Check');
    setInterval(async () => {
        if (polling || document.hidden) return;
        polling = true;
        try { await status(); } finally { polling = false; }
    }, 1000);
})();

const state = { actor: null, csrf: "", accounts: [] };
const page = document.querySelector("#page");
const loginView = document.querySelector("#login-view");
const appView = document.querySelector("#app-view");

async function api(path, options = {}) {
  const headers = { ...(options.body ? { "content-type": "application/json" } : {}), ...(options.headers || {}) };
  if (options.method && options.method !== "GET") headers["x-csrf-token"] = state.csrf;
  const response = await fetch(path, { credentials: "same-origin", ...options, headers });
  if (response.status === 401) { showLogin(); throw new Error("请先登录"); }
  const type = response.headers.get("content-type") || "";
  const result = type.includes("application/json") ? await response.json() : await response.text();
  if (!response.ok) throw new Error(result.error || "操作失败");
  return result;
}

async function start() {
  try {
    const { actor } = await api("/api/session");
    if (!actor) return showLogin();
    state.actor = actor; state.csrf = actor.csrfToken;
    showApp();
  } catch { showLogin(); }
}

function showLogin() { state.actor = null; loginView.hidden = false; appView.hidden = true; }
function showApp() {
  loginView.hidden = true; appView.hidden = false;
  document.querySelector("#actor-name").textContent = `${state.actor.username} · ${state.actor.role === "admin" ? "管理员" : "成员"}`;
  document.querySelectorAll("[data-admin]").forEach((element) => element.hidden = state.actor.role !== "admin");
  route();
}

document.querySelector("#login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const form = event.currentTarget; const button = form.querySelector("button[type=submit]"); const error = form.querySelector("#login-error");
  button.disabled = true; error.textContent = "";
  try {
    const data = Object.fromEntries(new FormData(form));
    const result = await api("/api/login", { method: "POST", body: JSON.stringify(data) });
    state.actor = result.actor; state.csrf = result.actor.csrfToken; form.reset(); showApp();
  } catch (cause) { error.textContent = cause.message; } finally { button.disabled = false; }
});

document.querySelector("#logout-button").addEventListener("click", async () => {
  try { await api("/api/logout", { method: "POST" }); } finally { showLogin(); }
});
window.addEventListener("hashchange", route);

async function route() {
  if (!state.actor) return;
  const requested = location.hash.slice(1) || "dashboard";
  const current = requested === "users" && state.actor.role !== "admin" ? "dashboard" : requested;
  document.querySelectorAll(".sidebar a").forEach((link) => link.classList.toggle("active", link.dataset.page === current));
  page.innerHTML = '<div class="empty">正在读取…</div>';
  try {
    if (current === "dashboard") await renderDashboard();
    else if (current === "accounts") await renderAccounts();
    else if (current === "messages") await renderMessages();
    else if (current === "tasks") await renderTasks();
    else if (current === "audit") await renderAudit();
    else if (current === "users") await renderUsers();
    else location.hash = "dashboard";
  } catch (cause) { page.innerHTML = `<div class="empty form-error">${escapeHtml(cause.message)}</div>`; }
}

async function renderDashboard() {
  const data = await api("/api/dashboard");
  page.innerHTML = `<header class="page-header"><div><h1>运行概览</h1><p>共享邮箱、后台队列和厂商连接状态</p></div><button data-refresh>刷新</button></header>
    <section class="metric-grid" aria-label="关键指标">
      ${metric("登记邮箱", data.accounts.total, "info")}${metric("状态正常", data.accounts.ready, "good")}
      ${metric("等待 / 执行", `${data.tasks.queued} / ${data.tasks.running}`, "warn")}${metric("失败任务", data.tasks.failed, "bad")}
    </section>
    <section class="panel"><h2 class="panel-title">厂商连接闸门</h2>${table(["厂商", "连续失败", "最近错误", "暂停至", "下次连接"], data.providerGates.map((row) => [row.provider, row.consecutive_failures, row.last_failure_code || "—", formatDate(row.paused_until), formatDate(row.next_connect_at)]))}</section>`;
  page.querySelector("[data-refresh]").onclick = route;
}

async function renderAccounts() {
  const data = await api("/api/accounts"); state.accounts = data.items;
  page.innerHTML = `<header class="page-header"><div><h1>邮箱</h1><p>所有成员共享查看；连接操作统一进入队列</p></div><div class="actions"><button data-import>批量导入</button><button data-export>导出清单</button><button class="primary" data-add>登记邮箱</button></div></header>
    <div class="panel"><div class="table-wrap"><table><thead><tr><th>邮箱</th><th>厂商</th><th>状态</th><th>收 / 发</th><th>上次同步</th><th><span class="visually-hidden">操作</span></th></tr></thead><tbody>
    ${data.items.map((row) => `<tr><td><strong>${escapeHtml(row.address)}</strong><br><span class="muted">${escapeHtml(row.display_name || "")}</span></td><td>${escapeHtml(providerName(row.provider))}</td><td>${status(row.status)}${row.last_error_code ? `<br><span class="muted">${escapeHtml(row.last_error_code)}</span>` : ""}</td><td>${ability(row.capability_receive)} / ${ability(row.capability_send)}</td><td class="numeric">${formatDate(row.last_synced_at)}</td><td><div class="row-actions"><button data-task="sync" data-id="${row.id}">同步</button><button data-send data-id="${row.id}" data-address="${escapeAttr(row.address)}">发件</button>${state.actor.role === "admin" ? `<button class="danger" data-delete data-id="${row.id}" data-address="${escapeAttr(row.address)}">删除</button>` : ""}</div></td></tr>`).join("") || '<tr><td colspan="6" class="empty">尚未登记邮箱</td></tr>'}
    </tbody></table></div></div>`;
  bindAccountActions();
}

function bindAccountActions() {
  page.querySelector("[data-add]").onclick = () => document.querySelector("#account-dialog").showModal();
  page.querySelector("[data-export]").onclick = () => location.href = "/api/accounts/export";
  page.querySelector("[data-import]").onclick = importCsv;
  page.querySelectorAll("[data-task]").forEach((button) => button.onclick = async () => {
    button.disabled = true; try { await api(`/api/accounts/${button.dataset.id}/tasks`, { method: "POST", body: JSON.stringify({ kind: button.dataset.task }) }); toast("任务已进入队列"); } catch (e) { toast(e.message); } finally { button.disabled = false; }
  });
  page.querySelectorAll("[data-send]").forEach((button) => button.onclick = () => {
    const form = document.querySelector("#send-form"); form.accountId.value = button.dataset.id; document.querySelector("#send-from").textContent = `发件邮箱：${button.dataset.address}`; document.querySelector("#send-dialog").showModal();
  });
  page.querySelectorAll("[data-delete]").forEach((button) => button.onclick = async () => {
    const confirmation = prompt(`此操作会删除邮箱及其本地邮件，且无法撤销。\n请输入完整邮箱地址确认：\n${button.dataset.address}`);
    if (confirmation !== button.dataset.address) return;
    try { await api(`/api/accounts/${button.dataset.id}`, { method: "DELETE", body: JSON.stringify({ confirm: confirmation }) }); toast("邮箱已删除"); await renderAccounts(); } catch (e) { toast(e.message); }
  });
}

document.querySelector("#account-form").addEventListener("submit", async (event) => {
  event.preventDefault(); const form = event.currentTarget; const error = form.querySelector(".form-error"); const submit = form.querySelector("button[type=submit]");
  error.textContent = ""; submit.disabled = true;
  try {
    const values = Object.fromEntries(new FormData(form));
    const body = { address: values.address, displayName: values.displayName, provider: values.provider,
      imapHost: values.imapHost || undefined, imapPort: values.imapPort ? Number(values.imapPort) : undefined,
      smtpHost: values.smtpHost || undefined, smtpPort: values.smtpPort ? Number(values.smtpPort) : undefined,
      credential: { type: "password", username: values.username, secret: values.secret } };
    await api("/api/accounts", { method: "POST", body: JSON.stringify(body) }); form.reset(); form.closest("dialog").close(); toast("邮箱已保存，正在排队验证"); await renderAccounts();
  } catch (e) { error.textContent = e.message; } finally { submit.disabled = false; }
});
document.querySelector("#account-form [name=provider]").addEventListener("change", (event) => document.querySelector("#custom-endpoints").hidden = event.target.value !== "custom");

document.querySelector("#send-form").addEventListener("submit", async (event) => {
  event.preventDefault(); const form = event.currentTarget; const error = form.querySelector(".form-error"); const submit = form.querySelector("button[type=submit]");
  submit.disabled = true; error.textContent = "";
  try {
    const values = Object.fromEntries(new FormData(form)); const to = String(values.to).split(",").map((v) => v.trim()).filter(Boolean);
    await api(`/api/accounts/${values.accountId}/send`, { method: "POST", body: JSON.stringify({ to, subject: values.subject, text: values.text }) });
    form.reset(); form.closest("dialog").close(); toast("邮件已进入发送队列");
  } catch (e) { error.textContent = e.message; } finally { submit.disabled = false; }
});

async function importCsv() {
  const input = document.createElement("input"); input.type = "file"; input.accept = ".csv,text/csv";
  input.onchange = async () => {
    const file = input.files?.[0]; if (!file) return;
    if (file.size > 1_500_000) return toast("文件不能超过 1.5 MB");
    try { const result = await api("/api/accounts/import", { method: "POST", body: JSON.stringify({ csv: await file.text() }) }); toast(`已处理 ${result.results.length} 行`); await renderAccounts(); } catch (e) { toast(e.message); }
  };
  input.click();
}

async function renderMessages() {
  const data = await api("/api/messages");
  page.innerHTML = `<header class="page-header"><div><h1>邮件</h1><p>最近同步的收件箱邮件</p></div></header>
    <form class="filters" id="message-search"><label>搜索主题、发件人或正文<input name="q" type="search"></label><button type="submit">搜索</button></form>
    <div class="panel" style="margin-top:16px"><div class="table-wrap"><table><thead><tr><th>发件人</th><th>主题</th><th>邮箱</th><th>时间</th><th>内容</th></tr></thead><tbody id="message-rows">${messageRows(data.items)}</tbody></table></div></div>`;
  page.querySelector("#message-search").onsubmit = async (event) => { event.preventDefault(); const q = new FormData(event.currentTarget).get("q"); const result = await api(`/api/messages?q=${encodeURIComponent(q)}`); page.querySelector("#message-rows").innerHTML = messageRows(result.items); bindMessageRows(); };
  bindMessageRows();
}
function messageRows(items) { return items.map((row) => `<tr class="clickable"><td>${escapeHtml(row.sender_name || row.sender_address)}</td><td><button class="quiet" data-message="${row.id}">${escapeHtml(row.subject || "（无主题）")}</button></td><td>${escapeHtml(row.address)}</td><td class="numeric">${formatDate(row.received_at)}</td><td>${row.content_loaded ? "已读取" : "按需读取"}</td></tr>`).join("") || '<tr><td colspan="5" class="empty">暂无邮件</td></tr>'; }
function bindMessageRows() { page.querySelectorAll("[data-message]").forEach((button) => button.onclick = () => openMessage(button.dataset.message)); }
async function openMessage(id) {
  const { item } = await api(`/api/messages/${id}`); const dialog = document.querySelector("#message-dialog"); const detail = document.querySelector("#message-detail");
  detail.innerHTML = `<header><button class="icon-button" style="float:right" aria-label="关闭" title="关闭" data-close>×</button><h2>${escapeHtml(item.subject || "（无主题）")}</h2><p>${escapeHtml(item.sender_name || "")} &lt;${escapeHtml(item.sender_address)}&gt;</p><p class="muted">${formatDate(item.received_at)} · ${escapeHtml(item.address)}</p></header>
    ${item.text_body ? `<div class="body">${escapeHtml(item.text_body)}</div>` : `<div class="empty"><p>正文尚未读取</p><button class="primary" data-load-content>读取正文和附件</button></div>`}
    ${item.attachments?.length ? `<h3>附件</h3><ul>${item.attachments.map((a) => `<li>${a.available ? `<a href="/api/attachments/${a.id}">${escapeHtml(a.filename)}</a>` : escapeHtml(a.filename)} <span class="muted">${bytes(a.size_bytes)}</span></li>`).join("")}</ul>` : ""}`;
  detail.querySelector("[data-close]").onclick = () => dialog.close();
  detail.querySelector("[data-load-content]")?.addEventListener("click", async (event) => { event.currentTarget.disabled = true; try { await api(`/api/messages/${id}/content`, { method: "POST", body: "{}" }); toast("正文读取任务已进入队列"); dialog.close(); } catch (e) { toast(e.message); event.currentTarget.disabled = false; } });
  dialog.showModal();
}

async function renderTasks() {
  const { items } = await api("/api/tasks");
  page.innerHTML = `<header class="page-header"><div><h1>后台任务</h1><p>交互任务优先；失败任务显示可审查的脱敏原因</p></div><button data-refresh>刷新</button></header><div class="panel">${table(["邮箱", "类型", "状态", "尝试", "可执行时间", "错误"], items.map((r) => [r.address, taskName(r.kind), status(r.state), `${r.attempts} / ${r.max_attempts}`, formatDate(r.available_at), r.last_error_summary || "—"]))}</div>`;
  page.querySelector("[data-refresh]").onclick = route;
}
async function renderAudit() {
  const { items } = await api("/api/audit");
  page.innerHTML = `<header class="page-header"><div><h1>审计记录</h1><p>成员操作、拒绝访问和破坏性动作</p></div></header><div class="panel">${table(["时间", "成员", "动作", "对象", "结果"], items.map((r) => [formatDate(r.created_at), r.username || "系统", r.action, `${r.target_type}${r.target_id ? ` · ${r.target_id}` : ""}`, status(r.outcome)]))}</div>`;
}
async function renderUsers() {
  const { items } = await api("/api/users");
  page.innerHTML = `<header class="page-header"><div><h1>成员</h1><p>最多 10 名，共同管理全部邮箱</p></div></header>
    <form id="user-form" class="panel" style="padding:16px;margin-bottom:16px"><div class="form-grid"><label>用户名<input name="username" minlength="3" required></label><label>角色<select name="role"><option value="member">成员</option><option value="admin">管理员</option></select></label><label>初始密码<input name="password" type="password" minlength="12" required autocomplete="new-password"></label><div style="align-self:end;margin:14px 0"><button class="primary" type="submit">创建成员</button></div></div><p class="form-error" role="alert"></p></form>
    <div class="panel">${table(["用户名", "角色", "状态", "最后活动", "创建时间"], items.map((r) => [r.username, r.role === "admin" ? "管理员" : "成员", r.is_active ? "启用" : "停用", formatDate(r.last_seen_at), formatDate(r.created_at)]))}</div>`;
  page.querySelector("#user-form").onsubmit = async (event) => { event.preventDefault(); const form = event.currentTarget; const error = form.querySelector(".form-error"); error.textContent = ""; try { await api("/api/users", { method: "POST", body: JSON.stringify(Object.fromEntries(new FormData(form))) }); toast("成员已创建"); await renderUsers(); } catch (e) { error.textContent = e.message; } };
}

function metric(label, value, kind) { return `<div class="metric ${kind}"><span>${label}</span><strong>${escapeHtml(String(value ?? 0))}</strong></div>`; }
function table(headers, rows) { return `<div class="table-wrap"><table><thead><tr>${headers.map((h) => `<th>${escapeHtml(h)}</th>`).join("")}</tr></thead><tbody>${rows.map((row) => `<tr>${row.map((cell) => `<td>${typeof cell === "string" && cell.startsWith('<span class="status') ? cell : escapeHtml(String(cell ?? "—"))}</td>`).join("")}</tr>`).join("") || `<tr><td colspan="${headers.length}" class="empty">暂无记录</td></tr>`}</tbody></table></div>`; }
function status(value) { return `<span class="status ${escapeAttr(value)}">${escapeHtml(statusName(value))}</span>`; }
function statusName(value) { return ({ ready: "正常", pending: "待验证", degraded: "异常", disabled: "停用", queued: "等待", running: "执行中", succeeded: "成功", failed: "失败", cancelled: "已取消", success: "成功", denied: "已拒绝", failure: "失败" })[value] || value; }
function taskName(value) { return ({ validate: "验证", sync: "同步", send: "发件", content: "读取正文" })[value] || value; }
function providerName(value) { return ({ gmail: "Gmail", outlook: "Outlook", qq: "QQ 邮箱", "163": "163 邮箱", custom: "自定义" })[value] || value; }
function ability(value) { return value === true ? "可用" : value === false ? "不可用" : "待定"; }
function formatDate(value) { return value ? new Intl.DateTimeFormat("zh-CN", { dateStyle: "short", timeStyle: "short", hour12: false }).format(new Date(value)) : "—"; }
function bytes(value) { const size = Number(value); return size < 1024 ? `${size} B` : size < 1048576 ? `${(size / 1024).toFixed(1)} KB` : `${(size / 1048576).toFixed(1)} MB`; }
function escapeHtml(value) { return String(value ?? "").replace(/[&<>"']/g, (char) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[char]); }
function escapeAttr(value) { return escapeHtml(value).replace(/`/g, "&#96;"); }
let toastTimer; function toast(message) { const element = document.querySelector("#toast"); element.textContent = message; element.classList.add("visible"); clearTimeout(toastTimer); toastTimer = setTimeout(() => element.classList.remove("visible"), 3200); }

start();

# 本机启动

进入目录：

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/蔻姿邮箱助手/mail-archiver-main"
```

1. 安装并启动 Docker Desktop。
2. 执行 `sh scripts/setup-local.sh`。它会在可用空间不少于 100 GiB 时生成本机密钥和管理员密码；不会在终端打印密码。
3. 执行 `docker compose up -d --build`。
4. 在浏览器访问 `http://127.0.0.1:5000`。

服务只绑定本机回环地址。自动同步默认开启：后台每 30 秒检查一次，到期账号默认每 6 小时同步一次，并在 6 小时内分散账号任务，避免大量账号同时连接 IMAP。打开收件箱后可点击“刷新邮件”立即同步当前账号。默认只同步 INBOX 中最近 30 天的邮件；认证失败、限流或同步水位未推进时退避 5 分钟再试。

管理员账号、数据库密码以及同步参数都在 `.env` 控制。复制 `.env.example` 为 `.env` 后修改：

```env
POSTGRES_PASSWORD=...
MAILARCHIVE_ADMIN_USERNAME=admin
MAILARCHIVE_ADMIN_PASSWORD=...
MAILARCHIVE_SYNC_INTERVAL_SECONDS=21600
MAILARCHIVE_SYNC_LOOKBACK_DAYS=30
MAILARCHIVE_SYNC_INBOX_ONLY=true
MAILARCHIVE_SYNC_MAX_CONCURRENT=4
MAILARCHIVE_SYNC_STARTUP_STAGGER_SECONDS=21600
```

5000 个账号可以放在同一个 PostgreSQL 中，但不适合每个账号都保持秒级同步。账号列表已使用服务端分页，后台任务会错峰且限制并发；日常优先依靠每 6 小时同步和打开收件箱后的手动刷新。若需要更快的自动同步，可以降低 `MAILARCHIVE_SYNC_INTERVAL_SECONDS`，同时观察 IMAP 服务商限流、CPU、内存和数据库写入量。

CSV 只接受固定表头：`email,app_password,group`。仅 Yahoo 与 GMX 可导入；系统自动设置 IMAP 和 TLS。请只在本机 CSV 中填写应用专用密码，导入完成后从下载目录移除该 CSV。

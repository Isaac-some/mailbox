# 本机启动

进入目录：

```sh
cd /path/to/Kouzi/mail-archiver-main
```

1. 安装并启动 Docker Desktop。
2. 执行 `sh scripts/setup-local.sh`。它会生成本机数据库密码和邮箱凭据加密密钥。
3. 执行 `docker compose up -d --build`。
4. 在浏览器访问 `http://127.0.0.1:5000`。

服务只绑定本机回环地址。邮箱不会定时自动同步；只有打开邮箱或点击“刷新邮件”时，系统才会按需连接并同步当前账号。默认只同步 INBOX 中最近 30 天的邮件，连接任务会限制并发，避免大量账号同时连接 IMAP。

本机免登录模式的数据库密码和同步参数由 `.env` 控制：

```env
POSTGRES_PASSWORD=...
MAILARCHIVE_SYNC_LOOKBACK_DAYS=30
MAILARCHIVE_SYNC_INBOX_ONLY=true
MAILARCHIVE_SYNC_MAX_CONCURRENT=4
```

账号列表使用服务端分页；批量导入不设置邮箱总数上限，但文件大小、单批大小和并发数仍受控，以保护内存和服务商连接额度。创建、编辑和导入不会连接邮箱，邮件内容只在用户主动打开或刷新时同步。

CSV 最小表头为：`邮箱（必填）,授权凭据（必填）,域名（可选）,Client ID（可选：Outlook OAuth2 Refresh Token 必填）`。授权凭据可以是 IMAP 密码、SMTP 密码、同时覆盖收发件的密码、Google 应用专用密码或 OAuth2 Refresh Token；Outlook OAuth2 Refresh Token 必须同时提供 Client ID。未知域名会按 `imap.域名` / `smtp.域名` 尝试，最终以主动连接结果为准。请只在本机 CSV 中填写凭据，导入完成后从下载目录移除该 CSV。

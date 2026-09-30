# 本地与香港单机部署

## 本地试跑

进入目录：

```sh
cd /Users/isaac/Downloads/邮箱/mailbox/mailbox-service-v2
npm install
make keys
cp .env.example .env
INITIAL_ADMIN_PASSWORD='replace-with-12-plus-chars' npm run seed:admin
npm run dev
```

另开终端运行 Worker：

```sh
cd /Users/isaac/Downloads/邮箱/mailbox/mailbox-service-v2
npm run dev:worker
```

浏览 `http://127.0.0.1:4300`。开发数据库存放在 `.data/local-postgres`，不提交 Git。

## 香港服务器

只购买一台香港地域、2 vCPU、2 GB RAM、40 GB 磁盘的试用月实例。先配置域名 A 记录指向固定公网 IPv4，再在服务器执行：

```sh
cd /opt/mailbox-service-v2
cp .env.production.example .env.production
make keys
chmod 600 .env.production secrets/*.key
docker compose up -d --build
```

安全组只开放 TCP 80/443 和 SSH 管理来源；不要开放 4300、5432、IMAP 或 SMTP 端口。邮件连接使用上游 IMAP 993、SMTP 465/587，不使用 TCP 25。

## 备份与恢复演练

备份必须同时保存 PostgreSQL dump、`secrets/credential.key`、`secrets/session.key` 和 `.env.production`。密钥与 dump 必须分开权限控制，但恢复时缺一不可。

```sh
cd /opt/mailbox-service-v2
make backup BACKUP_DIR=/var/backups/mailbox-service-v2
make restore DUMP=/var/backups/mailbox-service-v2/mailbox-YYYYMMDD-HHMMSS.dump
docker compose ps
curl -fsS https://mail.example.com/health/ready
```

恢复演练必须验证：管理员能登录、历史审计可读、随机邮箱的密文能被 Worker 解密、会话仍有效、任务状态没有重复领取。

## 观测与试运行

先用 100 个覆盖 Gmail、Outlook、QQ、163、自定义域的测试邮箱运行 7 天，每天模拟 500 个活跃邮箱。每天记录任务成功率、队列长度、厂商暂停时间、磁盘增长、RSS 内存和公网 IP 是否被持续阻断。达到成功率 98%、内存峰值低于 1.7 GB 且无连续 15 分钟整机阻断后，才扩展到 10,000 个登记邮箱。

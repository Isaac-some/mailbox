# 线上部署

线上部署从空数据库开始。GitHub 仓库、Docker 镜像和 Compose 文件都不包含邮箱账号、邮件、数据库或密钥。

## 权限规则

- 管理员生成一次性授权码和管理用户。
- 用户使用授权码注册后，可以添加或批量导入自己的邮箱。
- 所有人（包括管理员）只能查看、同步、导入、导出和删除自己名下的邮箱及邮件。
- 授权码使用一次后失效，可设置 1、7 或 30 天有效期，也可以提前撤销。

## 服务器启动

在服务器克隆仓库后进入项目目录：

```sh
cd /opt/kouzi/mail-archiver-main
cp .env.production.example .env.production
mkdir -p secrets
openssl rand -base64 32 > secrets/credential_encryption_key
chmod 600 .env.production secrets/credential_encryption_key
```

编辑 `.env.production`，替换数据库密码和管理员密码，然后启动：

```sh
cd /opt/kouzi/mail-archiver-main
docker compose --env-file .env.production -f docker-compose.production.yml up -d --build
```

应用只监听服务器本机的 `127.0.0.1:5000`。必须使用 Nginx 或 Caddy 提供域名和 HTTPS，不要把 5000 端口直接开放到公网。

管理员首次登录后，点击右上角钥匙图标生成授权码。内部用户在登录页选择“使用授权码注册”。

## 数据与备份

邮件正文和附件保存在 Docker 卷 `kouzi-mail-assistant_postgres_data`，登录会话密钥保存在 `kouzi-mail-assistant_data_protection_keys`。邮箱密码由 `secrets/credential_encryption_key` 加密。

备份必须同时包含：

- PostgreSQL 数据库；
- `data_protection_keys` 卷；
- `secrets/credential_encryption_key`。

不要把 `.env.production`、`secrets/`、数据库备份或邮件导出文件提交到 GitHub。

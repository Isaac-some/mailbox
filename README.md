# 粉裤子邮箱助手 Web 版

在浏览器中运行的邮箱归档与检索系统。此仓库只包含 Web 版：Docker 同时启动应用和 PostgreSQL，浏览器访问本机地址即可使用。

macOS DMG 是独立发布物，不在本仓库中构建或上传，避免把本机程序、历史邮件、数据库和密钥混入 GitHub。

## 快速启动

前提：已安装并打开 Docker Desktop。

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/蔻姿邮箱助手/mail-archiver-main"
./scripts/setup-local.sh
docker compose up -d --build
```

浏览器打开 [http://127.0.0.1:5000](http://127.0.0.1:5000)。首次登录账号是 `.env` 中的 `MAILARCHIVE_ADMIN_USERNAME`，密码是 `MAILARCHIVE_ADMIN_PASSWORD`。

查看服务状态或日志：

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/蔻姿邮箱助手/mail-archiver-main"
docker compose ps
docker compose logs -f mailarchive-app
```

停止服务但保留邮件归档数据：

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/蔻姿邮箱助手/mail-archiver-main"
docker compose down
```

## 保留旧浏览器数据

早期版本把运行数据错误地放进 `Data/`，该目录同时包含 C# 源码。新版本把运行数据放入独立的 `.runtime/`。已有旧数据时，先停止旧服务，再运行一次迁移脚本；它只复制，不删除旧数据。

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/蔻姿邮箱助手/mail-archiver-main"
docker compose down
./scripts/migrate-legacy-runtime.sh
docker compose up -d --build
```

## GitHub 上传前检查

以下内容已被忽略，不能上传：`.env`、`secrets/`、`.runtime/`、旧版 `Data/` 下的运行数据、`local-app/`、编译结果和日志。它们可能包含登录口令、凭据加密密钥、归档邮件或 macOS 安装包。`Data/MailArchiverDbContext.cs` 是必需源码，会正常上传。

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/蔻姿邮箱助手/mail-archiver-main"
git init
git add .
git status
```

确认暂存区没有上述私有文件后，再在 GitHub 新建空仓库并按 GitHub 页面显示的命令推送。不要提交 `.env` 或 `secrets/credential_encryption_key`；丢失凭据加密密钥会使已保存的邮箱密码无法解密。

## 安全边界

- 默认仅监听本机 `127.0.0.1:5000`，不会直接暴露到互联网。
- 要在局域网或公网提供服务，必须先配置 HTTPS 反向代理，再有针对性地修改端口绑定。
- PostgreSQL 数据在 `.runtime/postgres/`；数据保护密钥在 `.runtime/data-protection-keys/`。备份时必须同时备份两者和 `secrets/credential_encryption_key`。

## 开发与验证

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/蔻姿邮箱助手/mail-archiver-main"
dotnet test MailArchiver.sln
docker build -t kouzi-mail-assistant-web .
```

本项目基于 [Mail Archiver](https://github.com/s1t5/mail-archiver) 修改，按仓库中的 GPL-3.0 许可证发布。

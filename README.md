# 邮箱助手源码

本地版支持 Gmail、Yahoo、GMX、Outlook 和自定义域名邮箱收发件。自定义域名会先尝试标准 Autoconfig，失败后回退到 `imap.<域名>` 与 `smtp.<域名>`，并以用户主动连接验证结果为准。创建、编辑和导入不会立即连接邮箱；打开邮箱、点击刷新或精确搜索完整邮箱地址时，会优先同步邮件。写信支持纯文本、抄送和多个附件（默认最多 10 个、合计 10MB）。Gmail 优先应用专用密码，Yahoo/GMX 优先 IMAP 密码认证，失败后才尝试可用 OAuth；Outlook 优先 OAuth Refresh Token，收件 OAuth 失败后回退 IMAP 密码，发件按 Graph OAuth → SMTP OAuth → SMTP 密码依次尝试。

可逐个授权，也可与其他服务商混合导入 `邮箱<TAB>密码<TAB>Client ID<TAB>Refresh Token` 格式的 TXT；程序逐行识别，密码会加密保存，仅在 OAuth 失败时作为回退。收件使用 IMAP，Outlook 发件优先使用 Microsoft Graph `Mail.Send`，再尝试 SMTP OAuth 和 SMTP 密码。浏览器部署版默认关闭发件；Windows/macOS 本地版默认开启。

## 源码开发（.NET 10）

在本项目当前目录运行：

```sh
cd "/Users/isaac/Downloads/邮箱/mailbox"
dotnet restore MailArchiver.sln
dotnet test MailArchiver.sln
dotnet run --project MailArchiver.csproj
```

macOS 一键构建并运行临时测试 App（不会生成 DMG）：

```sh
cd "/Users/isaac/Downloads/邮箱/mailbox"
./script/build_and_run.sh --verify
```

也可以直接点击 Codex 项目的“Run”动作。临时 App 输出到 `/private/tmp/mail-assistant-run/app/邮箱助手.app`。

## Outlook 使用流程

1. 在“邮箱账号”里选择“Outlook 个人邮箱”，填写邮箱地址并保存。
2. 按微软设备授权页面提示登录并同意 IMAP 与 SMTP 权限。
3. 授权完成后可同步收件；账号状态显示“Outlook 可收发”后，可点“写邮件”。
4. 如果提示“邮件已经发送，但‘已发送’副本保存失败”，不要重复发送；先到收件方或 Outlook“已发送”确认。

发件成功后，程序会把副本保存到 Outlook“已发送”，并立即归档到本地。日志不会记录 OAuth Token、正文或附件内容。

## CSV 邮箱凭据导入（v2.4.4）

导入任务只显示保存成功与导入失败；后台验证错误不混入导入结果。点击“后台验证”后显示通知，打开指定邮箱会优先验证和收信。桌面端跟随使用者自己的系统网络/VPN，忽略历史应用代理配置。

检查更新从 `Isaac-some/mailbox-releases` 最新正式 Release 获取签名清单，发现新版后自动下载、校验和重启安装。每次发布必须同时上传 `desktop-update.json`、`desktop-update.sig`、Mac 更新 ZIP、Windows ZIP；只上传 DMG/EXE 不会触发应用更新。

账号来源为手动 CSV 导入，已关闭启动、搜索、刷新、授权识别和发件准备过程中的平台账号拉取。旧配置和环境变量不能重新开启它；发布包不再编译或注入平台 Token。

进入“邮箱账号 → 导入 CSV”，沿用四字段格式。重复导入同一邮箱会完整覆盖凭据、清除旧访问令牌、重新进入待验证状态；不重复创建账号，不删除已归档邮件。导入只保存，不连接服务商；验证和收信直接连接邮箱服务商。

后台按服务商独立并行，总计最多 8 个；Outlook、GMX、Gmail 每家最多 2 个，Yahoo 因实测限流限制为 1 个。同一厂商的不同域名共享额度。手动打开邮箱使用独立通道，并取消该账号当前的后台验证，避免等待整批任务。服务商限流后，该厂商后台冷却 60 秒；网络握手和服务商响应仍有实际耗时。

Mac 构建：

```sh
cd "/Users/isaac/Downloads/邮箱/mailbox"
bash script/build-local-macos-release.sh
```

Windows 可在 Mac 上交叉构建：

```sh
cd "/Users/isaac/Downloads/邮箱/mailbox"
bash script/build-windows-on-macos.sh
```

Windows 对外交付为单个 `windows-app/build/MailAssistant-Windows-x64-v2.4.4-Setup.exe`，包含应用、后端和运行时，安装后创建桌面快捷方式并保留本机邮箱数据。Mac 构建工具需预装 NSIS：`brew install makensis`。ZIP 便携版作为备用。

## 后续打包

当前版本 v2.4.4 的实测和验收记录见 [验收报告](doc/acceptance-v2.4.4-20261118.md)。重新打包时执行：

```sh
cd "/Users/isaac/Downloads/邮箱/mailbox"
dotnet build --configuration Release
./local-app/build-dmg.sh
```

Windows 需在 Windows PowerShell 中执行：

```powershell
cd "C:\path\to\mail-archiver-main"
.\windows-app\build-windows.ps1
```

两端构建完成后可生成全部带签名的发布资产（不会上传 GitHub，输出目录应为空）：

```sh
cd "/Users/isaac/Downloads/邮箱/mailbox"
python3 script/prepare-desktop-release.py --output "/Users/isaac/Downloads/邮箱/待发布版本"
```

两个外壳都加载同一套服务端源码。DMG 中只放应用和 Applications 拖拽入口，测试文档保留在源码目录。

## 浏览器版快速启动

浏览器版仍可用于内部邮箱收件与检索；默认不开放写信。

前提：已安装并打开 Docker Desktop。

```sh
cd /path/to/MailAssistant/mail-archiver-main
./scripts/setup-local.sh
docker compose up -d --build
```

浏览器打开 [http://127.0.0.1:5000](http://127.0.0.1:5000)，会直接进入邮箱账户页面，无需账号或密码。

查看服务状态或日志：

```sh
cd /path/to/MailAssistant/mail-archiver-main
docker compose ps
docker compose logs -f mailarchive-app
```

停止服务但保留邮件归档数据：

```sh
cd /path/to/MailAssistant/mail-archiver-main
docker compose down
```

## 保留旧浏览器数据

早期版本把运行数据错误地放进 `Data/`，该目录同时包含 C# 源码。新版本把运行数据放入独立的 `.runtime/`。已有旧数据时，先停止旧服务，再运行一次迁移脚本；它只复制，不删除旧数据。

```sh
cd /path/to/MailAssistant/mail-archiver-main
docker compose down
./scripts/migrate-legacy-runtime.sh
docker compose up -d --build
```

## GitHub 上传前检查

以下内容不能上传：`.env`、`secrets/` 中的真实密钥、`.runtime/`、旧版 `Data/` 下的运行数据、上传/导出文件、编译结果、日志以及现有 DMG/EXE。`local-app/`、`windows-app/` 和 `Data/MailArchiverDbContext.cs` 是必需源码，应当保留；仅排除它们各自的 `build/bin/obj` 产物。

```sh
cd /path/to/MailAssistant/mail-archiver-main
git init
git add .
git status
```

确认暂存区没有上述私有文件后，再在 GitHub 新建空仓库并按 GitHub 页面显示的命令推送。不要提交 `.env` 或 `secrets/credential_encryption_key`；丢失凭据加密密钥会使已保存的邮箱密码无法解密。

## 安全边界

- 默认仅监听本机 `127.0.0.1:5000`，不会直接暴露到互联网。
- 线上部署使用 `docker-compose.production.yml`，强制开启登录并要求管理员密码。
- 线上部署步骤见 `PRODUCTION_DEPLOYMENT.md`，必须配置 HTTPS 反向代理。
- PostgreSQL 数据在 `.runtime/postgres/`；数据保护密钥在 `.runtime/data-protection-keys/`。备份时必须同时备份两者和 `secrets/credential_encryption_key`。

本项目基于 [Mail Archiver](https://github.com/s1t5/mail-archiver) 修改，按仓库中的 GPL-3.0 许可证发布。

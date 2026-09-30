# 邮箱助手源码

本地版支持 Gmail、Yahoo、GMX、Outlook 和自定义域名邮箱收发件。自定义域名会先尝试标准 Autoconfig，失败后回退到 `imap.<域名>` 与 `smtp.<域名>`，并以用户主动连接验证结果为准。创建、编辑和导入不会自动连接邮箱；只有打开邮箱或点击刷新时才同步邮件。写信支持纯文本、抄送和多个附件（默认最多 10 个、合计 10MB）。Gmail 优先应用专用密码，Yahoo/GMX 优先 IMAP 密码认证，失败后才尝试可用 OAuth；Outlook 优先 OAuth Refresh Token，收件 OAuth 失败后回退 IMAP 密码，发件按 Graph OAuth → SMTP OAuth → SMTP 密码依次尝试。

可逐个授权，也可与其他服务商混合导入 `邮箱<TAB>密码<TAB>Client ID<TAB>Refresh Token` 格式的 TXT；程序逐行识别，密码会加密保存，仅在 OAuth 失败时作为回退。收件使用 IMAP，Outlook 发件优先使用 Microsoft Graph `Mail.Send`，再尝试 SMTP OAuth 和 SMTP 密码。浏览器部署版默认关闭发件；Windows/macOS 本地版默认开启。

## 源码开发（.NET 10）

在本项目当前目录运行：

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/邮箱助手/mail-archiver-main"
dotnet restore MailArchiver.sln
dotnet test MailArchiver.sln
dotnet run --project MailArchiver.csproj
```

macOS 一键构建并运行临时测试 App（不会生成 DMG）：

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/邮箱助手/mail-archiver-main"
./script/build_and_run.sh --verify
```

也可以直接点击 Codex 项目的“Run”动作。临时 App 输出到 `/private/tmp/mail-assistant-run/app/邮箱助手.app`。

## Outlook 使用流程

1. 在“邮箱账号”里选择“Outlook 个人邮箱”，填写邮箱地址并保存。
2. 按微软设备授权页面提示登录并同意 IMAP 与 SMTP 权限。
3. 授权完成后可同步收件；账号状态显示“Outlook 可收发”后，可点“写邮件”。
4. 如果提示“邮件已经发送，但‘已发送’副本保存失败”，不要重复发送；先到收件方或 Outlook“已发送”确认。

发件成功后，程序会把副本保存到 Outlook“已发送”，并立即归档到本地。日志不会记录 OAuth Token、正文或附件内容。

## 平台邮箱凭据同步

发布版本机 App 不显示登录页。启动时使用打包时注入的租户 token 自动增量拉取账号凭据，用户不需要填写账号、token、服务地址或导入 CSV。

构建发布包时在终端隐式输入 Token，脚本会把它注入安装包。接口地址已有默认值。Token 会进入最终安装包，因此拿到安装包的人可以提取并使用它；不要把安装包公开发布：

```zsh
cd "/Users/isaac/Downloads/邮箱/mailbox"
read -rs "MAIL_ASSISTANT_UPSTREAM_TOKEN?请输入租户 Token: "
echo
export MAIL_ASSISTANT_UPSTREAM_TOKEN
./script/build-local-macos-release.sh
unset MAIL_ASSISTANT_UPSTREAM_TOKEN
```

Windows 发布包在 PowerShell 中使用同名环境变量，先进入工作区：

```powershell
cd "C:\path\to\mailbox"
$secureToken = Read-Host "请输入租户 Token" -AsSecureString
$env:MAIL_ASSISTANT_UPSTREAM_TOKEN = [System.Net.NetworkCredential]::new("", $secureToken).Password
.\windows-app\build-windows.ps1
Remove-Item Env:MAIL_ASSISTANT_UPSTREAM_TOKEN
```

已有本机生成的 Mac App 时，也可以直接在 Mac 上交叉构建 Windows ZIP，复用 Mac 包中的同一租户 Token：

```zsh
cd "/Users/isaac/Downloads/邮箱/mailbox"
MAIL_ASSISTANT_UPSTREAM_TOKEN="$(jq -r '.UpstreamMailboxSync.BearerToken' 'local-app/build/邮箱助手.app/Contents/Resources/server/appsettings.Local.json')" bash ./script/build-windows-on-macos.sh
```

Windows 交付文件为 `windows-app/build/MailAssistant-Windows-x64-v2.3.5.zip`。解压整个文件夹后双击 `邮箱助手.exe`；不能只复制 exe。

服务地址变化时，再设置 `MAIL_ASSISTANT_UPSTREAM_ENDPOINT` 为完整的 HTTPS 接口地址。缺少 Token 时，发布构建会直接失败。

平台接口返回的 Outlook `credential` 会作为 Refresh Token 保存，`client_id` 会作为 Client ID 保存；单条坏数据只会被跳过，不会阻塞其他账号同步。

分页请求固定串行执行，每页最多 1000 条；只有全部页面成功后才保存第一页返回的 `serverTimestamp`。token 错误会提示 HTTP 401，服务端 HTTP 500 会按 5 秒间隔最多重试 3 次。

## 后续打包

本次源码交付不包含 DMG/EXE。需要发布时再执行：

```sh
cd "/Users/zhaoxiaohandexinwanju/Documents/邮箱助手/mail-archiver-main"
dotnet build --configuration Release
./local-app/build-dmg.sh
```

Windows 需在 Windows PowerShell 中执行：

```powershell
cd "C:\path\to\mail-archiver-main"
.\windows-app\build-windows.ps1
```

两个外壳都加载同一套服务端源码，因此重新打包后会获得相同的 Outlook 功能。

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

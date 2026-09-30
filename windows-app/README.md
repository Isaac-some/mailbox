# Windows 打包

在 Windows PowerShell 中生成 64 位发布文件夹。构建时输入租户 Token，用户打开应用时无需登录：

```powershell
cd "C:\path\to\mailbox"
$secureToken = Read-Host "请输入租户 Token" -AsSecureString
$env:MAIL_ASSISTANT_UPSTREAM_TOKEN = [System.Net.NetworkCredential]::new("", $secureToken).Password
.\windows-app\build-windows.ps1
Remove-Item Env:MAIL_ASSISTANT_UPSTREAM_TOKEN
```

接口地址已有默认值。服务地址变化时，再设置 `MAIL_ASSISTANT_UPSTREAM_ENDPOINT` 为完整的 HTTPS 接口地址。缺少 Token 时构建会失败。Token 会进入发布文件夹，不要公开分发。

已有 Mac 交付包时，也可以在 Mac 工作区运行 `bash ./script/build-windows-on-macos.sh` 交叉构建；命令需通过 `MAIL_ASSISTANT_UPSTREAM_TOKEN` 提供同一 Token。具体命令见项目根目录 README。

输出目录为 `windows-app\build\邮箱助手-Windows-x64`。把整个文件夹复制到 Windows 电脑后，双击 `邮箱助手.exe` 即可使用；不要只复制这个 exe，因为 `server` 文件夹包含应用本体。

数据存放在 `%LOCALAPPDATA%\MailAssistant`，不在安装目录。备份该目录即可保留本机邮件、账号和加密密钥。系统缺少 Microsoft Edge WebView2 Runtime 时，应用会自动在默认浏览器中打开。

应用菜单“窗口”提供“桌面窗口”“手机窄窗”和“始终置顶”。窗口模式、两种模式各自的尺寸和置顶状态会保存在 `%LOCALAPPDATA%\MailAssistant\window-preferences.json`，切换窗口模式不会重新加载当前邮件页面。

打包时会内置 .NET 运行时，目标电脑无需另装 .NET、Docker 或 PostgreSQL。

# Windows 打包

生成 64 位 Windows 发布文件夹：

```powershell
cd "C:\path\to\mail-archiver-main"
.\windows-app\build-windows.ps1
```

输出目录为 `windows-app\build\邮箱助手-Windows-x64`。把整个文件夹复制到 Windows 电脑后，双击 `邮箱助手.exe` 即可使用；不要只复制这个 exe，因为 `server` 文件夹包含应用本体。

数据存放在 `%LOCALAPPDATA%\KouziMailAssistant`，不在安装目录。备份该目录即可保留本机邮件、账号和加密密钥。Windows 10/11 需要 Microsoft Edge WebView2 Runtime；大多数系统已自带，缺少时应用会提示安装。

打包时会内置 .NET 运行时，目标电脑无需另装 .NET、Docker 或 PostgreSQL。

# 邮箱助手 v2.4.5 · Windows

对外只需发送 `MailAssistant-Windows-x64-v2.4.5-Setup.exe`，双击完成安装，通过桌面“邮箱助手”打开。安装程序已包含全部后端和 .NET 运行时，无需另发 `server` 文件夹。系统缺少 WebView2 时会在默认浏览器打开。

ZIP 为可选便携版，需要解压完整文件夹；安装版不需要 ZIP。

本版仅使用手动 CSV 导入账号。启动、搜索、刷新和发件准备均不会向旧平台拉取账号，也不需要平台 Token。同邮箱重复导入会覆盖凭据并重新等待验证，保留已下载邮件。

后台按服务商独立并行：Outlook、GMX、Gmail 每家 2 个，Yahoo 1 个。打开指定邮箱优先处理，不等待批量验证；网络和服务商响应仍需时间。出现限流时该厂商后台冷却 60 秒。

本机数据位于 `%LOCALAPPDATA%\MailAssistant`。升级不需要清空数据；该目录含账号、邮件和加密密钥，请妥善保留。

开发者先安装 NSIS（https://nsis.sourceforge.io/），再在 Windows PowerShell 打包；脚本默认同时生成完整 EXE 安装程序：

```powershell
cd "C:\path\to\mailbox"
.\windows-app\build-windows.ps1
```

也可在 Mac 工作区交叉构建：

```sh
cd "/Users/isaac/Downloads/邮箱/mailbox"
bash script/build-windows-on-macos.sh
```

Mac 首次需要安装 NSIS：`brew install makensis`。已有完整发布目录时，可单独生成安装 EXE：

```sh
cd "/Users/isaac/Downloads/邮箱/mailbox"
bash script/build-windows-installer.sh
```

导入结果只显示导入成功与失败。后台验证不展示统计，打开邮箱优先验证并获取邮件。网络跟随本机系统与 VPN，不使用应用内保存的手动代理。“检查更新”发现 GitHub 新版后自动下载、校验并重启。

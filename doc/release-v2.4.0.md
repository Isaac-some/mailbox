# 邮箱助手 v2.4.0 · 应用内更新

新增 Mac、Windows 应用内更新：检查新版本、查看更新内容、下载进度、重启并自动安装，无需每次到 Release 页面下载安装包。

- Mac：菜单“邮箱助手 → 检查更新…”。
- Windows：顶部菜单“检查更新”。
- 两个平台也可从“本机数据 → 检查更新”进入。
- 更新保留账号、邮件、加密密钥及本机设置。正在发送邮件或导入账号时，等待任务结束后再安装。
- 更新包带独立签名并校验完整性；安装或新版本启动失败时保留或恢复原程序。
- 使用公开签名清单检查版本，无需 GitHub 登录，也不依赖 GitHub API 的匿名请求配额。

**v2.3.9 及之前版本需手动安装本版一次，之后即可在应用内更新。**

## 下载

- [Windows x64 ZIP](https://github.com/Isaac-some/mailbox-releases/releases/download/v2.4.0/MailAssistant-Windows-x64-v2.4.0.zip)：完整解压，打开“邮箱助手.exe”，保留 server 文件夹。
- [macOS Apple Silicon DMG](https://github.com/Isaac-some/mailbox-releases/releases/download/v2.4.0/MailAssistant-AppleSilicon-v2.4.0.dmg)：M 系列芯片、macOS 15+，拖到“应用程序”后打开。

更新 ZIP 供应用自动安装使用；Mac 手动安装请使用 DMG。

macOS 延续本地签名，尚未经过 Apple 公证。GitHub 下载需本机网络能访问 github.com 和 release-assets.githubusercontent.com。

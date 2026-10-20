# 邮箱助手 v2.3.7 · Windows 与 macOS

公开下载安装，无需登录 GitHub 或申请仓库权限：

- [Windows x64 · 下载 ZIP](https://github.com/Isaac-some/mailbox-releases/releases/download/v2.3.7/MailAssistant-Windows-x64-v2.3.7.zip)：完整解压后打开“邮箱助手.exe”，保留同目录的 server 文件夹。内置 .NET，无需另装数据库。
- [macOS Apple Silicon · 下载 DMG](https://github.com/Isaac-some/mailbox-releases/releases/download/v2.3.7/MailAssistant-AppleSilicon-v2.3.7.dmg)：适用于 M 系列芯片、macOS 15 及以上。拖到“应用程序”后打开。

VPN 请使用能覆盖 GitHub 下载的节点或全局模式，确保 `github.com` 和 `release-assets.githubusercontent.com` 均可访问。仅打开 VPN 开关不能保证节点或分流规则能访问这些域名。两平台下载均无公司内网或 GitHub 账号限制。

- 下载入口公开，无需登录 GitHub 或获得仓库权限。此前 404 的原因是交付仓库为私有仓库。
- 平台授权在构建时编译进程序，不再写入安装包的明文 JSON 配置；交付仓库不提供授权源文件。
- “本机数据”增加“下载接口检测报告”，记录本次进程实际发出的平台请求编号、时间、HTTP 状态、分页及本地数量；请求携带相同的 `X-Request-ID`，可在后端逐条核对。报告不含 Token、邮箱地址、密码和邮件正文。
- 安装包不含测试数据库或预置邮件，首次启动从平台真实拉取账号授权；搜索、打开邮箱或刷新时按需拉取邮件。
- Mac DMG 附带后端测试说明，包含独立空数据目录的启动方式与失败验证步骤；Windows ZIP 内的 README 也说明接口报告与网络要求。

账号列表显示的是本地保存的账号，已有缓存可以在接口失败时继续显示；这不能证明本次接口调用成功。账号接口成功也不等于邮件已从服务商下载。

本次未宣称已定位或修复后端所报的全部失败日志。请使用检测报告的请求编号，在后端环境对照具体失败请求。

Windows 交付验证：88 项 Windows 回归测试通过；从公开链接下载的最终 ZIP 经 SHA-256 校验后，在独立 Windows 环境实际解压、启动原生应用，创建空 SQLite 数据库并通过内置授权拉取真实平台账号、导出接口报告。两个平台的公开安装包均在该环境完成匿名下载及 SHA-256 校验。[查看验证运行](https://github.com/Isaac-some/mailbox/actions/runs/39389532225)。

公开二进制可被分析并提取内置授权，编译进程序不是保密机制；本次按用户要求公开下载。应用使用本地签名，尚未经过 Apple 公证。

# 邮箱助手 v2.3.9

公开下载，无需登录 GitHub：

- [macOS Apple Silicon · DMG](https://github.com/Isaac-some/mailbox-releases/releases/download/v2.3.9/MailAssistant-AppleSilicon-v2.3.9.dmg)
- [Windows x64 · ZIP](https://github.com/Isaac-some/mailbox-releases/releases/download/v2.3.9/MailAssistant-Windows-x64-v2.3.9.zip)
- [完整 Release 与 SHA-256 校验](https://github.com/Isaac-some/mailbox-releases/releases/tag/v2.3.9)

写信入口移到每个邮箱账号旁，点击后自动选择该账号。发件账号通过同一个搜索框添加，支持移除账号块，最多 20 个账号分别向一个收件人发信。待验证账号也能搜索和选择，选中后排队更新授权并检测发件能力；部分发送成功时，重试只保留失败账号。

发件任务改为“生成发件 CSV → 上传并发件 → 发件历史”三个步骤，已有 CSV 可以直接跳过第一步。账号列表按需分页加载，避免大账号库一次生成上万行页面。补充返回箭头并统一页头对齐。

保留此前的 CSV 导入、域名自动发现与配置复用、收件同步、定时发送及附件功能。

本地验证：807 项完整回归通过；最后调整后 62 项针对性检查通过；Mac 成品启动、交互、七种 CSV 及域名配置回归通过。Mac DMG 和 Windows ZIP 已生成并完成完整性检查。

用户已确认验收通过，按此版本封板。Mac 使用本地签名，尚未经过 Apple 公证。

安装方式：Mac 打开 DMG，把“邮箱助手”拖入“应用程序”；Windows 完整解压 ZIP 后打开“邮箱助手.exe”，保留同目录的 server 文件夹。已有账号与邮件数据继续保留。

最终公开安装包已在独立 Windows 环境匿名下载：Mac DMG 与 Windows ZIP 的 SHA-256 均与验收包一致；公开 ZIP 实际解压、原生启动、创建空 SQLite，并通过内置授权从平台拉取真实账号，接口报告包含 HTTP 200 请求记录。[查看最终验证](https://github.com/Isaac-some/mailbox/actions/runs/39544610527)。

# v2.4.1 发布验收

日期：2026-10-21（Asia/Shanghai）。

本版仅调整邮箱连接错误提示并升级版本号，未修改收发路由或数据库结构。源码与标签：`307a754f7ad087c0ed4d9735e9c414dd30a4f334` / `v2.4.1`。

- 完整回归：819 通过，0 失败，0 跳过。原始记录：`.runtime/release-v241/test-results/full.trx`。
- [双平台原生升级验收](https://github.com/Isaac-some/mailbox/actions/runs/39588860915)：Mac Apple Silicon、Windows x64 均从正式 v2.4.0 升级到 v2.4.1 候选，正常升级、下载损坏拦截及启动失败回退共六个场景全部通过。每个场景均检查账号、邮件、密钥、网络设置保留，验证任务执行时阻止安装；使用隔离目录与合成数据，关闭平台拉取和发件。
- 正式版复用已验收候选的 Mac DMG、Mac 更新 ZIP、Windows ZIP，仅使用正式标签和 URL 重新生成签名清单；三个公开包的 GitHub SHA-256 与本地验收成品一致。
- Mac DMG 校验通过；打包阶段应用深度严格签名检查通过。两平台成品均确认包含新提示且 JSON 设置不含明文平台 Token。
- 发布后实际启动 v2.4.0 Mac 应用，用独立数据目录检查普通稳定渠道，未指定测试标签：返回 `available`，当前版本 `2.4.0`，最新版本 `2.4.1`。记录：`.runtime/release-v241/stable-channel-old-version/report.json`。

正式版本：[v2.4.1](https://github.com/Isaac-some/mailbox-releases/releases/tag/v2.4.1)。原始升级场景证据：`.runtime/release-v241/update-matrix-evidence/`；构建及校验清单随 Release 发布。

Mac 延用本地签名，未经过 Apple 公证。本轮未切换用户 VPN，也未修改日常账号、邮件或密钥；原有 `local-app/AppIcon.icns` 本地删除未纳入提交。独立回归数据库已停止。

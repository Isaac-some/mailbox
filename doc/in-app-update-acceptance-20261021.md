# 应用内更新验收 · v2.4.0

日期：2026-10-21（Asia/Shanghai）。

## 最终交付

最终候选为 `v2.4.0-rc.3`，正式版 `v2.4.0` 复用该候选的 Mac DMG、Mac 更新 ZIP 和 Windows ZIP，不重新构建程序。正式清单更换为正式标签、正式下载 URL 和版本说明，重新签名。清单和 `build-manifest.json` 标识源码、成品校验值及验证记录。

功能源码提交 `431d582`；验证脚本含后续 Windows UTF-8 修正。正式版源码标签见仓库 `v2.4.0`。

## 验证证据

- 最终源码完整回归：819 通过，0 失败，0 跳过。原始结果 `.runtime/update-v240/test-results/final3-full.trx`。
- [双平台实际升级矩阵](https://github.com/Isaac-some/mailbox/actions/runs/39560392608)：Mac Apple Silicon 与 Windows 均从公开的 `v2.3.92-update-test` 基线升级到 `v2.4.0-rc.3`。
- [Mac 最终成品先行通过记录](https://github.com/Isaac-some/mailbox/actions/runs/39560001242)：该 run 的 Mac job 三个场景通过；Windows job 因 Python 默认编码读取中文清单失败，随后仅修正验收脚本的 UTF-8 读取，未修改安装包。
- [最终成品启动与匿名下载验证](https://github.com/Isaac-some/mailbox/actions/runs/39559639764)：Windows 原生启动，Win/Mac 公开下载 SHA-256 校验，实际平台账号接口成功。归档邮件数为 0；这不证明真实邮件收发。
- Mac 本机独立目录完整升级也已通过：`.runtime/update-v240/mac-success-final/report.json`（前一候选 rc.2）。最终 rc.3 由 Apple Silicon 云端实际原生升级覆盖。

双平台每个平台的三个场景：

| 场景 | 验收内容 |
| --- | --- |
| 正常升级 | 应用实际从 GitHub 下载签名清单与 ZIP，退出、替换、重新打开；健康响应版本与本次 nonce 匹配 |
| 下载损坏 | 只损坏测试数据目录的下载 ZIP；安装前拒绝，原程序与进程继续运行 |
| 新版启动失败 | 只破坏测试安装目录内已校验的候选启动程序；原生安装程序实际恢复旧程序并重新打开 |

每个场景都验证：CSRF 缺失请求被拒绝；合成发件任务处于 Sending 时阻止安装；清除该状态后可继续；合成账号、合成邮件、本机加密密钥、网络设置和设置保留文件均不变。后台发件与平台拉取始终关闭，没有真实发信。

## 隔离与限制

测试使用新建安装目录、账号库、密钥和独立端口。Windows 单实例锁按数据目录区分，Mac 使用独立偏好域及非持久 WebKit。日常账号库和密钥的文件摘要与测试前一致，证据 `.runtime/update-v240/daily-data-isolation.json`。

预发布均设置 `prerelease=true`、`latest=false`，未推送到普通用户的正式渠道。正式渠道使用公开签名清单，不调用 GitHub API；该改动来自 Mac 云端匿名 API 返回 403 的真实失败。更新签名私钥不进仓库或安装包。

Mac 使用本地签名，未经过 Apple 公证。回退针对程序文件；本版本没有引入数据库结构变更，未来不兼容数据迁移需要另行设计恢复方案。

发布后的公开入口和普通稳定渠道检查结果记录在 `.runtime/update-v240/published-verification.json`。

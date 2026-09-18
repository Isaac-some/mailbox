# v1.0.19 导入校验候选版（2026-09-03）

本次需求覆盖原验收记录中“不联网验证”的旧约定。当前流程：统一清理复制空白 → 基础输入检查 → 收件登录验证 → 通过后保存，不自动下载邮件。

## 实现与行为

- `MailCredentialInputPolicy` 负责 CSV/API/上游共用的规范化、非空、异常字符及长度上限检查。
- `MailCredentialIntakeService` 在脱离数据库跟踪、ID 为 0 的候选账号上验证，成功后才更新原账号；失败不会污染 EF 跟踪对象或旧凭据。归一化相同的历史输入保留轮换 token 及发件记忆。
- `MailCredentialVerifier` 复用服务商认证漏斗、代理、证书校验和自定义域名发现；每行 20 秒限时；不选文件夹，不下载，不发信。仅记为 `IncomingVerified` / `Imap`。
- Outlook 收件 OAuth 请求向下传递取消令牌，确保导入限时能终止网络请求。
- 平台 HTTP 获取与逐行登录使用独立的超时范围；有拒绝条目时停止所选邮箱的同步，已通过条目可以保留。
- Mac/Windows 版本均提升为 1.0.19。未提交、推送或发布 GitHub。

## 验证

- Release 构建通过；非外部 PostgreSQL 回归 478 通过、0 失败。
- `node tests/ui/mailbox-reader.mjs` 通过。
- 包内服务的隔离 HTTP 验证通过：新版页面/静态资源；空白清理后为空、控制字符、五列表头的上传均被拒绝且无数据库新增。
- Mac 严格 ad-hoc 签名验证和 DMG 校验通过；Windows 交叉发布及 ZIP 完整性、关键文件/版本检查通过。
- 没有真实平台地址、鉴权信息及测试邮箱，本次不声称真实服务商认证验收通过。Windows 窗口运行待用户实机验证。Mac 未 Developer ID 签名/Apple 公证。外部 PostgreSQL 测试未运行。

## 输出和复现

用户交付目录：`/Users/isaac/Downloads/邮箱/候选版-v1.0.19-20260903`。原 `候选版-20260903` 和根目录旧包未替换。

构建/测试证据保存在本机 `/private/tmp/mailbox-1.0.19-*.log`。打包临时目录为 `/private/tmp/mailbox-v1.0.19-20260903`；隔离 HTTP 脚本为其中的 `smoke.py`，不依赖用户真实数据。

```sh
cd /Users/isaac/Downloads/邮箱/mailbox
/private/tmp/mail-assistant-dotnet-sdk/dotnet test tests/MailArchiver.Tests/MailArchiver.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName!~DeletionPolicyApplicationServiceTests&FullyQualifiedName!~SyncJobServiceTests&FullyQualifiedName!~BandwidthServiceTests&FullyQualifiedName!~EmailCoreServiceTests&FullyQualifiedName!~ApiKeyServiceTests&FullyQualifiedName!~AccountStorageServiceTests&FullyQualifiedName!~AccessLogServiceTests&FullyQualifiedName!~ProviderEmailServiceFactoryTests&FullyQualifiedName!~UserServiceTests'
node tests/ui/mailbox-reader.mjs
```

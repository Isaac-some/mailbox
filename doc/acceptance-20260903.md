# 四字段导入与认证回退验收（2026-09-03）

> 历史候选版记录。导入不联网的规则已由后续需求调整；当前行为与验证见 [v1.0.19 验收记录](acceptance-v1.0.19.md)。

## 交付范围

- CSV 模板固定 `email,domain,credential,client_id`；前两项中的 domain 可空，credential 必填，client_id 可空。兼容短中文表头和旧拼写 Cilent ID。
- 导入不判断凭据类型、不联网验证、不批量自动同步。重复邮箱全量替换四字段，同批最后一行优先。
- 同一不透明凭据供密码及 refresh-token 认证使用。Outlook 优先 OAuth，Gmail/Yahoo/GMX 优先密码；收件与发件分别记住成功方式。未变化的平台输入保留成功方式及服务商轮换的 token。
- Gmail 应用密码在认证时去掉分组空格和不可见格式字符。
- 可配置同步前 GET 平台账号，采用相同四字段契约。缺省关闭；接口失败不继续使用可能过期的账号信息。
- 工具栏不再被居中的最大宽度挤出左侧大块空白。收件箱阅读区使用已有安全 HTML 端点，保留邮件按钮样式，阻止脚本。
- 存储沿用 UTC，显示统一北京时间；手工输入同步时间也按北京时间解释。
- 收件箱刷新最终失败时显示并保留原因，不再悄悄重新加载；发件的未知网络结果不会自动重发，避免重复发送。

## 已验证

| 验收项 | 证据 |
| --- | --- |
| 最终 Release 编译 | 0 错误；存在项目原有可空引用和 NuGet 漏洞源访问警告 |
| 全部不依赖外部 PostgreSQL 的测试 | 464 通过，0 失败 |
| 聚焦需求的测试 | 130 通过，0 失败（包含在上述测试中） |
| 独立本地策略测试 | 75 通过，0 失败（与主测试有重叠，不相加） |
| 真实 HTTP 导入 | 固定四列表头；重复覆盖；末行优先；空值清除；五列表头拒绝；导入后状态未验证、LastSync 未变化 |
| 认证回退 | 模拟首次失败、次选成功、全部失败、记忆优先及平台重复拉取后保留记忆 |
| 安全 HTML | 浏览器实际看到蓝色按钮，class/style 保留，onclick/script 被清除，CSP 阻止脚本 |
| 北京时间 | UTC 00:30 在实际界面为 08:30；洛杉矶/东京宿主及 Z/+09:00/-07:00 输入结果一致 |
| 失败反馈 | 实际页面脚本的确定性测试：连接失败持续显示、上游错误显示、成功仅刷新一次 |
| macOS 包 | 内嵌服务启动、静态资源、配置、ad-hoc 严格签名结构及 DMG 校验 |
| Windows 包 | win-x64 交叉发布、启动程序/服务端文件存在、配置和静态资源、ZIP 完整性 |

## 未验证与使用边界

- 没有平台实际接口地址和鉴权信息，尚未进行真实平台联调。
- 没有在此次验收中使用用户真实邮箱凭据。认证重试只适用于服务商支持且具备必要参数的方式，不能令无效或撤销的授权码变为有效。
- 本机没有可用的测试 PostgreSQL，先前完整测试中的 180 项在数据库初始化阶段因主机无法解析而失败；不能据此宣称完整数据库测试通过。
- Windows 窗口启动需要 Windows 实机验收，本机只完成交叉构建和包结构校验。
- macOS 包是 ad-hoc 本地签名，没有 Developer ID 和 Apple 公证；首次运行可能需要在“隐私与安全性”手动允许。
- 原根目录的 v1.0.18 回滚 DMG/ZIP 未改动；新包位于 `候选版-20260903`，不是正式发布。

## 回归命令

```sh
cd /Users/isaac/Downloads/邮箱/mailbox
node tests/ui/mailbox-reader.mjs
/private/tmp/kouzi-dotnet-sdk/dotnet test tests/MailArchiver.Tests/MailArchiver.Tests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName!~DeletionPolicyApplicationServiceTests&FullyQualifiedName!~SyncJobServiceTests&FullyQualifiedName!~BandwidthServiceTests&FullyQualifiedName!~EmailCoreServiceTests&FullyQualifiedName!~ApiKeyServiceTests&FullyQualifiedName!~AccountStorageServiceTests&FullyQualifiedName!~AccessLogServiceTests&FullyQualifiedName!~ProviderEmailServiceFactoryTests&FullyQualifiedName!~UserServiceTests'
```

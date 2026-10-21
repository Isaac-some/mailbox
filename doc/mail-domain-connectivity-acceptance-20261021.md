# 邮箱助手 v2.3.8 · 域名适配与收发验收

日期：2026-10-21（北京时间）。本文件不包含真实邮箱地址、密码、Token 或邮件正文。

## 验收目标

新域名通过“已有域名配置 → 域名官方 Autoconfig → Thunderbird 公共配置库 → 安全连接验证 / 一次性手动配置”接入，无需为每个后缀编写模块。保存账号不依赖网络；邮箱连通性、授权及实际收发分别验证。域名知识只提供连接和认证尝试顺序，不能替其他账号确认密码有效。

用户追加的最终验收标准：从已有账号中随机选择一个 Gmail，与本次 CSV 中的 GMX 邮箱双向发送，并在各自收件侧实际找到唯一标识对应的邮件。仅 SMTP 接受、登录成功或发件箱有副本均不能代替收件确认。

## 根因与修复

CSV 的 `email,domain,credential,client_id` 四列表头可被现有解析器正确识别，导入阶段没有访问平台账号接口或邮件服务器。本次失败发生在收件连接阶段。

`gmx.es` 未命中内置域名，旧版退回猜测的 `imap.gmx.es`。本机实测该地址的证书不适用于此名称，触发 TLS 握手失败；`imap.gmx.com:993` 则通过证书与 TLS 验证。GMX 官方服务器说明与 Thunderbird 公共配置都指向 `imap.gmx.com`，SMTP 为 `mail.gmx.com`。

修复内容：

- 补全公共配置发现，按域名持久保存来源、收发地址、端口、加密方式、用户名模板、成功认证方式、验证时间和分别的失败原因。
- 同域名首次发现合并并发请求；配置跨账号和重启复用，发现失败短暂缓存，普通已发现配置七天后更新；手动配置持续有效。
- SSL/TLS 与 STARTTLS 按配置执行，公共配置中的明文连接不被采用。保留证书验证，没有通过忽略错误证书来规避问题。
- 为未被配置库覆盖的域名提供“本机数据 → 邮箱域名连接”入口；一次填写收发服务器及可选 OAuth 令牌地址，后续账号复用。
- OAuth 支持现有 Gmail / Yahoo / Microsoft 路径，并允许自定义域名配置标准 Refresh Token 流程。每个账号的 Client ID、Token、Secret 等仍属于该账号；不写入域名记录。
- 导入保留各服务商真实的 Client ID，排除导出文件将密码重复填入该列的情形。Gmail OAuth 令牌不再被批量验证的 16 位应用密码规则拦截。
- 更新 TLS 提示，包含服务器名称与证书不匹配、端口或加密方式错误等实际可能原因。

## 自动测试用例

| 编号 | 场景 | 预期结果 | 自动化位置 |
|---|---|---|---|
| D01 | GMX 国家域名、任意新域名、企业子域名 | 不增加域名代码即可发现收发服务器 | MailDomainConnectivityTests |
| D02 | 域名两个配置站点不可用，公共库可用 | 使用公共库；gmx.es 得到 imap.gmx.com | MailEndpointDiscoveryServiceTests / MailDomainConnectivityTests |
| D03 | 已发现后添加同域名其他账号，或程序重启 | 直接复用；无新增配置 HTTP 请求 | MailDomainConnectivityTests |
| D04 | 24 个同域名账号同时连接 | 首次发现仅执行一次 | MailDomainConnectivityTests |
| D05 | 无配置、离线、域名配置响应超时 | 不伪报已连通；短暂缓存失败，可补充配置 | MailDomainConnectivityTests |
| D06 | 993 SSL、143 STARTTLS、自定义加密端口 | 保留并执行明确加密方式 | MailDomainConnectivityTests |
| D07 | 配置返回明文或未知加密方式 | 不使用该配置 | MailDomainConnectivityTests |
| D08 | 非法主机、IP、localhost、内部后缀 | 不学习该服务器配置 | MailDomainConnectivityTests |
| D09 | 配置声称其他域名 | 不错误套用到当前邮箱 | MailDomainConnectivityTests |
| D10 | 非法 XML、外部实体、超过 64 KB | 拒绝该响应，继续后续配置源 | MailDomainConnectivityTests |
| D11 | 用户取消发现 | 传播取消，不写入虚假的发现结果 | MailDomainConnectivityTests |
| D12 | 收件 OAuth 成功，发件密码成功 | 分别记忆两种认证方式 | MailDomainConnectivityTests |
| D13 | 某个账号密码错误，其他账号此前成功 | 保留已知可用服务器；仅记录此次失败 | MailDomainConnectivityTests |
| D14 | 收件失败后发件成功 | 收件失败原因仍保留，两个方向独立 | MailDomainConnectivityTests |
| D15 | 修改服务器后，旧连接才返回成功 | 旧地址不能验证新地址 | MailDomainConnectivityTests |
| D16 | 并发保存 35 个域名 | 无覆盖丢失、无残留临时文件 | MailDomainConnectivityTests |
| D17 | 手动配置及用户名使用邮箱前缀 | 新旧账号直接使用，发现过程无需联网 | MailDomainConnectivityTests |
| D18 | 自定义 OAuth URL 不安全或非法 | 拒绝保存 | MailDomainConnectivityTests |
| D19 | 企业域名使用 Google 邮件主机 | 自动识别可配置的 Google OAuth 流程 | MailDomainConnectivityTests |
| D20 | 惯例主机实际登录成功 | 将实际连通的端点升级为域名知识 | MailDomainConnectivityTests |
| D21 | 域名记录文件 | 不含完整账号、密码、Access Token / Refresh Token | MailDomainConnectivityTests / CustomDomainOAuthConnectionTests |
| A01 | 新 OAuth 服务商，IMAP XOAUTH2 | 标准令牌刷新后，真实 MailKit 协议客户端完成认证 | CustomDomainOAuthConnectionTests |
| A02 | 新 OAuth 服务商，SMTP XOAUTH2 | 标准令牌刷新后，真实 SMTP 客户端完成认证 | CustomDomainOAuthConnectionTests |
| A03 | 密码不通过，但 OAuth 可用 | 自动回退，记住成功的 OAuth 方法 | CustomDomainOAuthConnectionTests |
| A04 | Token 刷新返回新 Refresh Token | 轮换结果保存到账号，域名文件不含 Token | CustomDomainOAuthConnectionTests |
| I01 | 完全离线导入混合七种域名 | 全部入库，处于待验证；不调用邮箱验证器 | CsvImportPipelineTests |
| I02 | 导入后网络校验失败 | 导入数量不回滚；网络失败与文件失败分开计数 | CsvImportPipelineTests |
| I03 | Gmail 长 OAuth 令牌及 Client ID | 不套用 16 位应用密码限制 | CsvImportPipelineTests |
| I04 | 批次部分成功、重试失败账号 | 仅重新验证失败账号，不重复导入 | CsvImportPipelineTests |
| I05 | Gmail / Yahoo / 新域名真实 Client ID | 按输入保存；与密码完全重复时不当作 Client ID | MailCredentialIntakeServiceTests |
| I06 | 新域名录入但没有连通验证 | 仍为待验证，不能继承他人收发能力 | MailCredentialIntakeServiceTests |
| U01 | 标准、中英文 BOM、分号、Tab、无表头、域名错位、跨文件重复 | 7 个有效账号保存，1 次覆盖，1 行空凭据失败，1 次域名纠正 | mailbox-domain-connectivity.mjs |
| U02 | 页面保存 IMAP STARTTLS、SMTP SSL、自定义 OAuth | 保存、回显及后续复用正确 | mailbox-domain-connectivity.mjs |
| U03 | 修改域名配置的请求缺少防伪令牌 | 返回 HTTP 400 | mailbox-domain-connectivity.mjs |
| U04 | 同步失败及平台接口失败 | 页面保留可理解的失败提示，成功时刷新列表 | mailbox-reader.mjs |

协议级 OAuth 测试使用本机模拟服务，不向真实邮箱发送邮件；真实双向收发单独记录。

## 真实 CSV 与邮件验证

已在独立空库、禁用平台自动拉取的环境中通过实际页面导入用户提供的 CSV：新增 1 个 GMX 账号，0 行失败。后台校验完成后，收件登录通过；点击刷新后拉取到 1 封真实邮件，SMTP 授权验证亦成功。域名文件记录正确服务器，账号状态为 `ImapSmtpVerified`。

浏览器七种 CSV 场景：7 个账号、1 次重复覆盖、1 行无凭据拒绝、1 次域名纠正；导入未启动联网验证，配置页面与 CSRF 测试通过，无页面脚本异常。

双向真实收发：**通过**。使用最终 Mac 安装包 v2.3.8（构建 238）启动隔离实例，通过实际页面录入、校验、刷新、写信和发送，平台自动拉取关闭。

从现有 340 个 Gmail 中随机抽取的首个账号保存的是 10 位普通密码，格式校验正确拒绝；随后从 270 个保存了 16 位凭据的账号中再次随机抽取一个，实际 IMAP 与 SMTP 授权均通过。未输出密码，未修改原账号库。

| 方向 | SMTP 发出时间（北京时间） | 对方收件确认时间 | 收件侧文件夹 | 结果 |
|---|---|---|---|---|
| 随机 Gmail → CSV 的 GMX | 11:24:41 | 11:27:01 | INBOX | 收到 |
| CSV 的 GMX → 同一 Gmail | 11:24:45 | 11:26:06 | [Gmail]/All Mail | 收到 |

两封邮件采用同一唯一批次标识 `MAILBOX-E2E-aafdd057-0647-4cb0-a9a9-b6dfd2b97225`，按收件账号、`IsOutgoing=false`、主题、正文标记、发送与接收两侧相同的 Message-ID、发件与收件身份交叉核验。本次 Gmail 出站地址去掉了用户名中的一个点，身份比较按 Gmail 等价地址归一化，Message-ID 与正文标记仍严格匹配。共实际发送两封邮件，未因测试重跑重复发送。

本机网络的独立限制：DNS 轮换返回的 Gmail SMTP 地址 `108.177.98.109` 连接超时，另两条本机解析所得地址 `142.250.107.108/.109` 的真实 SMTP 服务可达。验收期间仅为隔离实例使用临时本机 CONNECT 转发到可达地址；应用继续以 `smtp.gmail.com` 执行 STARTTLS 和完整证书校验。转发不解密邮件，不进入产品或安装包，不修改系统网络设置。此次验收证明实际收发能力，不能保证受限网络下所有 Gmail 地址均可直连；用户遇到同类网络限制需使用可连通的网络或代理。

最终安装包内再次执行七种 CSV 和域名配置浏览器测试，全部通过。

## 可实现范围

新后缀一般不需要改代码：有公开邮箱配置则自动发现，没有则配置一次。OAuth 首次授权仍取决于服务商开放的应用注册、授权范围及交互；当前通用 OAuth 配置接收服务商签发的 Refresh Token，并执行标准刷新认证。禁止 IMAP/SMTP、关闭第三方访问，或只提供专有 API 的服务商，不能仅凭邮箱后缀绕过限制。账户密码错误也不能通过复用域名知识解决。

## 执行与证据

```sh
cd /Users/isaac/Downloads/邮箱/mailbox
.local-tools/dotnet/dotnet test tests/MailArchiver.Tests/MailArchiver.Tests.csproj --no-restore --filter 'FullyQualifiedName~MailDomainConnectivityTests|FullyQualifiedName~MailEndpointDiscoveryServiceTests|FullyQualifiedName~MailCredentialIntakeServiceTests|FullyQualifiedName~CsvImportPipelineTests|FullyQualifiedName~CustomDomainOAuthConnectionTests'
node tests/ui/mailbox-reader.mjs
```

全套测试需独立 PostgreSQL；通用启动方式见 `script/test-all.sh`。本次使用本机隔离 PostgreSQL 完成全套 796 项回归，0 失败。

实际浏览器场景脚本为 `tests/ui/mailbox-domain-connectivity.mjs`，只允许明确指定的本机可写测试实例。真实测试的数据库、日志和截图保存在忽略目录 `.runtime/domain-connectivity-20261021/`，包含真实私有数据，不属于交付源码或安装包。

完整回归结果为 `results/domain-final.trx`，最终安装包页面测试结果为 `packaged-browser-flexibility.log`，真实双向证据为 `bidirectional-result.json`。测试完成后关闭隔离服务及临时转发，保留私有测试证据。

本地 Mac 测试安装包：`local-app/build-v2.3.8/MailAssistant-AppleSilicon-v2.3.8.dmg`。应用签名完整性及 DMG 校验通过，SHA-256：`a82f1ddd09c30b98718a5ee2bed565f90c6e0e80d5127232f3c0367a9006c9f4`。安装包不含测试数据库、凭据密钥、域名私有记录。Windows 源码版本同步为 2.3.8；本次未执行 Windows 真机验证或生成新的 Windows 安装包。

参考：[GMX 西班牙站服务器说明](https://support.gmx.es/pop-imap/imap/server.html)、[Thunderbird 公共邮箱配置库](https://github.com/thunderbird/autoconfig)。

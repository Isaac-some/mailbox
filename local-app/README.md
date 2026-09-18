# macOS 打包

仅支持 Apple Silicon。仓库可以把 .NET 10 SDK 和 NuGet 依赖保存在
`.local-tools/`，不需要修改系统环境。该目录不会提交到 Git。

首次或日常完整构建：

```sh
cd "/path/to/mailbox"
./script/build-local-macos-release.sh
```

第一次运行会从 Microsoft 官方地址下载固定版本的 Apple Silicon .NET 10
SDK，并把 SDK 与 NuGet 包缓存到工作区。如果本机已有 NuGet 缓存，会优先
使用 APFS 写时复制初始化工作区缓存，缺少的包再联网补齐。以后会直接复用
本地副本。仅准备 SDK：

```sh
cd "/path/to/mailbox"
./script/bootstrap-local-build-tools.sh
```

非标准 .NET 安装可以设置 `DOTNET_BIN` 和 `DOTNET_RUNTIME_ROOT`。如果脚本检测到运行时依赖外部 Brotli，还必须通过 `DOTNET_BROTLI_LIB_DIR` 指向包含 Brotli 动态库的 `lib` 目录；缺少该目录时构建会直接失败，不会生成不可移植的 App。

如果服务端已由 SDK 容器交叉发布为 `osx-arm64`，可设置
`SERVER_PUBLISH_DIR` 复用发布物；此时脚本只负责组装、签名和生成 DMG。

DMG 输出到 `local-app/build/MailAssistant-AppleSilicon-v<版本号>.dmg`。打包会内嵌 .NET 运行时，目标 Mac 无需安装 .NET 或 Docker。应用未使用 Developer ID 签名或 Apple 公证；首次打开需在 macOS 的“隐私与安全性”中手动允许。

只构建本机测试 App、不生成 DMG：

```sh
cd "/path/to/mailbox"
./script/build-local-macos-release.sh --app-only
```

测试 App 默认输出到 `/private/tmp/mail-assistant-test/邮箱助手.app`，避免云同步目录附加 Finder 属性后破坏严格签名校验。需要改目录时可设置 `MAIL_ASSISTANT_APP_OUTPUT_DIR`。脚本会执行完整的 ad-hoc 签名和校验；该签名仅保证 App 结构与本机加载完整性，不替代 Developer ID 签名和 Apple 公证。

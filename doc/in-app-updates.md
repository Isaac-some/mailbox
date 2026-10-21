# 应用内更新

Mac 菜单“邮箱助手 → 检查更新…”、Windows 菜单“检查更新”和“本机数据 → 检查更新”进入同一个更新页面。

用户主动检查、下载，然后点击“重启并更新”。下载进度留在应用中，离开页面后仍继续下载。首次使用需要手动安装支持更新的版本。

## 发布约定

正式渠道通过固定的 `releases/latest/download/desktop-update.json` 地址读取 `Isaac-some/mailbox-releases` 的签名更新清单，跳过草稿和预发布。不调用 GitHub REST API，避免共享 IP 的匿名 API 配额导致 403；无需仓库 Token。预发布仅能通过启动环境 `MAIL_ASSISTANT_UPDATE_TEST_TAG` 指定，网页请求不能修改渠道。

每个版本发布：Mac DMG、保留应用 bundle 的 Mac 更新 ZIP、Windows ZIP、`desktop-update.json`、`desktop-update.sig`、校验文件和构建清单。Manifest 内包含平台、版本、Release 标签、下载地址、文件大小、SHA-256 和包内目录。

更新公钥嵌入应用及独立安装程序。两者都校验 ECDSA P-256 签名，安装程序再次校验 ZIP 的大小和摘要。仅允许从指定公开仓库及 GitHub Release CDN 下载。

签名私钥不在源码、CI、安装包或公开 Release 中。本机默认路径：`~/Library/Application Support/MailAssistantRelease/update-signing-key.pem`，权限 0600。**发布者需保留并备份此密钥；丢失后不能随意换公钥继续为已安装用户发布更新。**

```sh
cd /Users/isaac/Downloads/邮箱/mailbox
python3 script/create-update-manifest.py \
  --version 2.4.0 --tag v2.4.0 \
  --mac local-app/build-v2.4.0/MailAssistant-AppleSilicon-v2.4.0-update.zip \
  --windows windows-app/build/MailAssistant-Windows-x64-v2.4.0.zip \
  --output .runtime/update-v240/stable
```

## 安装与恢复

下载写入本机数据目录的独立更新会话，不覆盖账号库或密钥。独立安装程序先解压至安装目录旁的临时目录，校验目录、版本、必要文件及 Mac bundle 签名，再通知主程序退出。

安装通过同卷目录重命名保留上一版；新程序需返回本次随机 nonce 和预期版本的本机健康响应。新版无法启动时恢复上一版程序。该恢复针对程序文件，不回滚用户数据或数据库结构；未来版本引入不兼容的数据迁移时，应单独设计迁移恢复方案。

发件服务与任务 worker 共用安装互斥锁。正在发送或导入时拒绝重启安装；进入安装后阻止新的发送和网页写操作。未来排期任务保留，应用重新打开后继续处理。

Mac 应先安装到可写目录；磁盘映像和 App Translocation 中拒绝原地更新。Mac 延续本地签名，尚未使用 Developer ID 或 Apple 公证，独立更新签名不替代 Apple 公证。

## 隔离验证

自动验收只使用新建目录、独立端口、合成账号和合成邮件；关闭平台账号拉取与发件。Mac 使用独立偏好域和非持久 WebKit，Windows 使用按测试数据目录区分的单实例锁。

`script/verify-desktop-update.py` 驱动实际成品，下载公开预发布并检查正常升级、下载损坏拒绝和新版启动失败后的程序回退，比较账号、邮件、加密密钥与设置。回退用例仅破坏本次测试已解压的候选程序，不改发布包和日常应用。

Windows 使用 `.github/workflows/verify-in-app-update.yml` 在真实 Windows runner 执行同样的用例。

# 试运行验收记录

## 已完成的本地门槛

- [x] 新分支 `codex/hong-kong-mail-service-v2`，新代码独立于旧应用目录。
- [x] `npm run check` TypeScript 严格检查通过。
- [x] `npm run build` 生产构建通过。
- [x] `npm test`：2 个测试文件、6 个断言通过。
- [x] `npm run check`：TypeScript 严格检查通过。
- [x] `npm run build`：生产构建通过。
- [x] PGlite migration：本地持久数据库建表成功。
- [x] Web 回归：首页 `200`、`/health/live` `200`、`/health/ready` `200`、未登录业务接口 `401`。
- [x] 端到端认证：管理员登录 `200`，登录后概览 `200`，缺失 CSRF token 的写请求 `403`。
- [x] `npm audit --omit=dev`：`found 0 vulnerabilities`。
- [ ] Worker 真实 IMAP/SMTP 连接：需测试邮箱和服务器网络，未在本地执行。
- [ ] Codex 标准安全扫描：工具预检因系统 Python 缺 `tomllib/tomli` 阻塞，未声称完成；代码级威胁模型见 `THREAT-MODEL.md`。

## 必须在真实试运行填写

| 指标 | 门槛 | 实测 |
|---|---:|---:|
| 并发成员浏览 | 10 人 | |
| 测试邮箱覆盖 | 100 个 / 7 天 | |
| 每日活跃邮箱 | <= 500 | |
| 有效凭证同步成功率 | >= 98% | |
| 连续整机 IP 阻断 | < 15 分钟 | |
| Worker/Web 内存峰值 | < 1.7 GB | |
| 任务重启后丢失 | 0 | |
| 备份恢复 | DB + 两把 key + 会话 | |

服务器购买、域名、真实邮箱凭证和付款均未执行。

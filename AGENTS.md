# AGENTS.md — AI 编码助手约定（开源版）

本文件面向在本仓库工作的 AI 编码助手与贡献者。进入任何目录工作前先读本文件，再读该目录的 README 与父级约定。用户在对话中的最新明确要求优先于本文件，但「红线」中的事项除外。

## 项目概览

通研带外系统管理（tongyaninfo-OOB）：面向 HP iLO4、Dell iDRAC 8 的带外管理工作台，支持服务端集中部署与本地桌面运行。

| 目录 | 内容 | 技术栈 |
|---|---|---|
| `backend/` | 服务端 API、采集、认证、MCP/OAuth | .NET 10、EF Core、Npgsql |
| `frontend/PC/` | PC 管理端 | React 19、Umi Max、Ant Design 6、TypeScript |
| `frontend/H5/` | 移动端（规划中，仅目录说明） | — |
| `desktop/` | 桌面宿主 | Electron、.NET 10、SQLite |
| `deploy/oob/` | 部署示例 | Docker/Compose、Nginx、systemd |
| `docs/` | 架构、开发、接口、ADR、ER、版本规约 | Markdown |
| `scripts/` | 仓库检查链 | Node 内置模块 |

入口文档：[README](README.md)、[开发手册](docs/development.md)、[架构](docs/architecture.md)、[接口合同](docs/api/README.md)、[检查链](scripts/README.md)。

## 按任务读取

| 修改范围 | 先读 | 主要检查 |
|---|---|---|
| 后端 | `backend/README.md`、`docs/architecture.md`、`docs/api/README.md` | `dotnet build`、`dotnet test`、接口合同 |
| PC 前端 | `frontend/PC/README.md`、`docs/api/` | `npm --prefix frontend/PC run lint`、`test`、`build` |
| 桌面 | `desktop/README.md` | `npm --prefix desktop run typecheck`、`test`、`build` |
| 数据库迁移 | `docs/releases/README.md`、`backend/src/*/Persistence/Migrations/` | 迁移台账、真实 PostgreSQL 验证 |
| 部署 | `deploy/README.md`、`deploy/oob/README.md` | 配置事实与凭据注入方式 |
| 文档 | `docs/README.md` | `node scripts/verify.mjs --scope docs` |

## 开发约定

- **接口**：统一返回 `ApiResult` 并保留 HTTP 状态码；预期业务失败不抛业务异常；健康检查、下载、流式按真实内容类型处理。
- **ID 与时间**：普通主键由 `IdService` 生成，`long` 在 JSON 中以十进制字符串传输；数据库存 UTC，展示默认 Asia/Shanghai，只转换一次。
- **数据库**：结构只由 EF 迁移管理，应用启动不自动建库/迁库；新增迁移须同步 `docs/releases/` 版本台账；每张表、每个字段写中文 `COMMENT ON`。
- **合同链**：后端路由与 DTO → `docs/api/oob/` → `frontend/PC/src/services/oob/generated.ts`。生成物不手改；接口变更须更新合同、重新生成并验证消费方。
- **前端**：遵循 `.editorconfig` 与 Biome；业务页面放在 `src/pages/oob/`，接口调用放在 `src/services/oob/`；不把演示或临时数据当成业务数据。
- **注释与文档**：说明触发条件、权限、状态变化与副作用，不复述代码；文档使用仓库相对链接。
- **安全**：MCP 查询只读；真实电源控制默认关闭；新增权限、证书或白名单逻辑须在服务端实现，不能只靠前端。

## 验证要求

改代码后运行受影响模块的真实检查（构建、测试、类型检查、合同检查、文档链接检查），如实报告结果；没跑的不要声称通过。

```sh
node scripts/doctor.mjs
node scripts/verify.mjs --scope docs
node scripts/verify.mjs
```

- 集成测试与数据库结构验证需要真实 PostgreSQL 17 + TimescaleDB；没有环境时只运行驱动测试，并明确记录跳过原因。
- 前端改动除自动化检查外，需在真实浏览器验证关键页面与控制台错误；设备、SMTP、外部 MCP 客户端验收分别记录。
- 修缺陷先复现，再补回归测试；测试固定时间与随机种子，只清理本次创建的数据。

## 红线

**任何情况下都不做：**

- 编造测试结果、验证结论、工作项编号或人工验收结论。
- 把真实凭据（BMC 账号密码、数据库连接串、SMTP 口令、证书私钥、token）写入源码、文档、前端包或提交历史。
- 提交真实 IP、主机名、设备序列号、客户名称、生产日志或个人信息；示例必须使用 RFC 5737 文档网段与合成名称。
- 把规划中的能力写成已实现，或用推测补齐产品事实。
- 代填他人的评审意见，或代签人工验收结论。

**须经用户明确确认后才执行：**

- 推送、强制推送、删除共享分支、修改分支保护或仓库设置。
- 使用生产凭据或生产数据库；在共享环境执行结构变更、删除数据或改共享配置。
- 超出任务范围的改动：顺手重构无关代码、升级无关依赖、改公共组件。

## 沟通

使用中文撰写提交信息、文档与注释；提交信息遵循 Conventional Commits。变更说明应区分「已实现」「已验证」「已验收」，三者不可互相替代。

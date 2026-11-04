# 仓库检查链

全部命令从仓库根执行，以脚本自身位置定位工程。检查链不调用 Git、不安装全局工具、不访问真实设备。

| 工具 | 职责 |
| --- | --- |
| `doctor.mjs` | 检查 Node/npm、.NET SDK 版本与工程清单；不安装依赖、不访问网络 |
| `bootstrap.mjs` | 按锁恢复合同工具、PC、桌面与后端 NuGet 依赖；显式执行本地 Umi setup |
| `verify.mjs` | docs/frontend/backend/all 范围检查；默认 all，失败立即停止 |
| `check-docs.mjs` | 检查 Markdown 本地相对路径是否存在 |
| `oob-contracts.mjs` | 从后端实际 MVC/端点与 JSON Schema 离线导出合同，逐字比较漂移 |
| `product-version.cjs` | PC 与桌面构建读取根 `VERSION` 的唯一入口 |
| `lib.mjs` | 共用定位、无 shell 进程与 npm CLI 解析 |

## 常用命令

```sh
node scripts/doctor.mjs
node scripts/bootstrap.mjs
node scripts/verify.mjs --scope docs
node scripts/verify.mjs --scope backend
node scripts/verify.mjs --scope frontend
node scripts/verify.mjs
node scripts/oob-contracts.mjs check
```

## 检查范围

| 范围 | 内容 |
| --- | --- |
| docs | 工具链、脚本语法、Markdown 本地链接 |
| backend | docs + 后端四层构建、驱动测试、桌面 .NET 构建与 SQLite 测试 + 合同检查 |
| frontend | docs + 合同检查 + PC setup/lint/test/build + 桌面 typecheck/test/build |
| all（默认） | 以上全部 |

- **集成测试**：`OobManagement.Integration.Tests` 需要真实 PostgreSQL 17 + TimescaleDB 的独立 Testing 库。默认不执行，输出跳过说明；配置数据库后使用 `OOB_INTEGRATION_DATABASE=1 node scripts/verify.mjs` 开启。跳过不能记为通过。
- **桌面 Electron 打包**：`bootstrap` 使用 `--ignore-scripts`，Electron 二进制需另行 `node desktop/node_modules/electron/install.js` 下载；完整安装包打包、签名与平台验收不属于本检查链。

## 合同链

后端实际 Controller / EndpointDataSource / DTO / JSON 序列化
→ `backend/tools/OobContracts`（独立元数据容器，不启动业务宿主）
→ `scripts/oob-contracts.mjs`（不读取私有配置或后台服务）
→ `docs/api/oob/routes.json`、`schemas.json`
→ `json-schema-to-typescript 16.0.0`（`frontend/package.json` 锁定）
→ `frontend/PC/src/services/oob/generated.ts`

`check` 将三份正式产物逐字比较，`generate` 才更新正式文件。生成物不手改；临时输出在仓库外，不自动删除。

## 环境要求

- Node.js >= 22（PC 工程要求）
- .NET SDK 10.0.300（见 [global.json](../global.json)）
- npm 与 .NET NuGet 可访问各自包源

工具通过不等于业务验收：浏览器、真实设备、SMTP、外部 MCP 客户端与人工电源验收需按 [CONTRIBUTING.md](../CONTRIBUTING.md) 另行执行并如实记录。

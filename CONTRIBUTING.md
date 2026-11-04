# 参与贡献

感谢你对通研带外系统管理的关注。本文说明如何提交 Issue、Pull Request 以及本地开发与验证要求。

## 行为准则

参与本项目讨论、评审和提交时，请保持尊重、聚焦技术问题，不发布真实客户信息、生产凭据、设备地址或任何个人信息。

## 提交 Issue

请在 GitHub Issues 中提交，并选择合适的模板：

- **缺陷报告**：使用 `Bug report` 模板，说明版本、环境、复现步骤、预期与实际结果；涉及设备时使用合成名称与文档地址段（如 `192.0.2.10`），不要填写真实内网地址与账号。
- **功能建议**：使用 `Feature request` 模板，说明使用场景与期望行为，不承诺未实现能力。
- **安全问题**：不要在公开 Issue 中报告，请按 [SECURITY.md](SECURITY.md) 的私密渠道提交。

## Pull Request 流程

1. Fork 本仓库并从 `main` 创建分支：
   - `feature/<简短英文主题>`：新增功能
   - `fix/<简短英文主题>`：缺陷修复
   - `docs/<简短英文主题>`：文档
   - `chore/<简短英文主题>`：构建、依赖、工具
2. 在分支内完成修改，保持变更聚焦；不顺手重构无关代码，不升级无关依赖。
3. 本地运行受影响的检查（见下节），在 PR 描述中**如实**填写执行过的命令与结果。
4. 提交 PR 到 `main`，描述中说明：
   - 变更目的与影响范围
   - 验证命令与真实结果（通过 / 失败 / 跳过及原因）
   - 是否涉及数据库迁移、接口合同或部署配置
   - 关联 Issue 编号（如有）
5. 维护者评审后合并。请勿在 PR 中代填他人评审意见或人工验收结论。

### 提交信息规范

采用 Conventional Commits：

```text
<type>(<scope>): <描述>
```

- `type`：`feat`、`fix`、`docs`、`style`、`refactor`、`perf`、`test`、`chore`、`ci`
- 描述使用中文或英文均可，1–50 字，冒号后保留一个空格
- 示例：`fix(oob): 修正历史查询缺测统计`

## 本地开发

### 环境要求

- .NET SDK 10.0.300（[global.json](global.json)）
- Node.js >= 22（`frontend/PC/package.json` 的 `engines`）
- PostgreSQL 17 + TimescaleDB 2.31（仅服务端与集成测试需要）

### 常用命令

```sh
node scripts/doctor.mjs            # 工具链检查
node scripts/bootstrap.mjs         # 恢复锁定依赖（PC/桌面/合同工具/.NET）
node scripts/verify.mjs --scope docs
node scripts/verify.mjs            # 完整检查：构建 + 测试 + 合同
```

分模块命令见 [README.md](README.md)、[backend/README.md](backend/README.md)、[frontend/PC/README.md](frontend/PC/README.md)、[desktop/README.md](desktop/README.md)。

### 代码与文档约定

- 遵循根目录 [.editorconfig](.editorconfig)；前端使用 `npm --prefix frontend/PC run biome` 格式化。
- 后端接口统一返回 `ApiResult` 并保留 HTTP 状态码；长 ID 以十进制字符串传输；时间存 UTC，展示默认 Asia/Shanghai。
- 数据库结构只由 EF 迁移管理，应用启动不自动建库或迁库；新增迁移须同时更新 `docs/releases/` 台账。
- 接口路由与 DTO 变更后，运行 `node scripts/oob-contracts.mjs generate` 更新 `docs/api/oob/` 与 `frontend/PC/src/services/oob/generated.ts`，并用 `check` 验证无漂移。
- 文档使用仓库相对路径链接，`node scripts/verify.mjs --scope docs` 会检查失效链接。

## 敏感信息红线

提交前请确认：

- 不提交真实 IP、主机名、设备序列号、BMC 账号密码、数据库连接串、SMTP 口令、证书私钥。
- 不提交 `.env`、`appsettings.*.Local.json`、`*.pfx`、`*.p12`、`*.key` 等私有文件（已在 [.gitignore](.gitignore) 中忽略）。
- 文档与示例使用 RFC 5737 文档网段（`192.0.2.0/24`、`198.51.100.0/24`、`203.0.113.0/24`）与合成设备名。
- 测试夹具不得包含真实环境数据或生产日志。

## 许可证

提交贡献即表示你同意以 [Apache License 2.0](LICENSE) 许可你的贡献；第三方组件与素材的许可见 [NOTICE](NOTICE)。

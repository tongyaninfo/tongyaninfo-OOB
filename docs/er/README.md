# 数据库与结构设计

本目录保存带外管理系统的数据库结构说明、ER 概念与设计资料。仓库位置：`docs/er`。

## 当前内容

- 数据库结构由 EF Core 迁移管理，源码位于 `backend/src/OobManagement.Infrastructure/Persistence/Migrations`（业务链）与 `Persistence/OAuthMigrations`（MCP OAuth 链）；桌面版 SQLite 迁移位于 `desktop/backend/OobManagement.Desktop.Storage/Persistence/Migrations`。
- 三条迁移链的完整清单、历史摘要与升级边界见[版本规约](../releases/README.md)与 [1.0.0 基线](../releases/1.0.0.md)。
- 本目录不复制可执行 SQL 或迁移脚本；结构变更只能通过 EF 迁移与明确执行入口完成。

## 设计资料

如需补充 ER 图或结构说明，使用可编辑源文件放在本目录，并在本文登记。设计图不构成执行 DDL 的授权。

- [仓库入口](../../README.md)
- [架构地图](../architecture.md)
- [文档中心](../README.md)

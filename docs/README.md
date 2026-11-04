# 文档中心

本目录是通研带外系统管理的文档入口，与仓库根 [README](../README.md) 一起构成对外文档。文档只描述已实现或明确标注为规划的内容。

## 入门

| 主题 | 文档 |
| --- | --- |
| 项目概览与快速开始 | [根 README](../README.md) |
| 架构与技术栈 | [架构地图](architecture.md) |
| 开发、配置与排错 | [开发与接手](development.md) |
| 术语 | [关键术语](glossary.md) |
| 接口合同 | [API 文档](api/README.md) |
| 版本与升级 | [版本规约](releases/README.md) |
| 设计决策 | [ADR](adr/README.md) |
| 一期需求与范围 | [一期实施方案](spec/带外管理系统一期实施方案.md) |
| 数据库设计 | [ER 说明](er/README.md) |

## 使用指南

- [中文使用指南](../frontend/PC/docs/user-guide.zh-CN.md)
- [English user guide](../frontend/PC/docs/user-guide.en-US.md)
- [繁體中文使用指南](../frontend/PC/docs/user-guide.zh-TW.md)

## 部署与集成

| 主题 | 文档 |
| --- | --- |
| 部署资源总览 | [deploy/README.md](../deploy/README.md) |
| 本机 Compose 与镜像 | [deploy/oob/README.md](../deploy/oob/README.md) |
| 宿主 systemd 发布模板 | [deploy/oob/HOST-SYSTEMD.md](../deploy/oob/HOST-SYSTEMD.md) |
| Podman 完整系统 | [deploy/oob/PODMAN.md](../deploy/oob/PODMAN.md) |
| 镜像仓库 Compose | [deploy/oob/harbor/README.md](../deploy/oob/harbor/README.md) |
| 桌面版说明 | [desktop/README.md](../desktop/README.md) |
| MCP 接入 | [docs/api/mcp/README.md](api/mcp/README.md) |

## 工程与协作

| 主题 | 文档 |
| --- | --- |
| 后端工程 | [backend/README.md](../backend/README.md) |
| PC 管理端 | [frontend/PC/README.md](../frontend/PC/README.md) |
| 检查链与脚本 | [scripts/README.md](../scripts/README.md) |
| 贡献流程 | [CONTRIBUTING.md](../CONTRIBUTING.md) |
| AI 协作约定 | [AGENTS.md](../AGENTS.md) |
| 安全策略 | [SECURITY.md](../SECURITY.md) |
| 变更记录 | [CHANGELOG.md](../CHANGELOG.md) |

## 文档约定

- 链接使用仓库相对路径，不写个人绝对路径、内网地址或真实账号。
- 示例使用 RFC 5737 文档网段（`192.0.2.0/24`、`198.51.100.0/24`、`203.0.113.0/24`）与合成设备名。
- 截图使用合成演示数据；引用截图时保留出处与许可说明。
- 提交前运行 `node scripts/check-docs.mjs` 检查本地链接。

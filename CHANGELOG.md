# 变更记录

本文件记录本仓库面向开源发布的主要变更。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循产品版本（根 [VERSION](VERSION)）。

## [1.0.2] - 2026-11-05

修复 Windows/Linux/macOS 桌面安装包构建与上传的平台差异问题。

### 修复

- Windows：离线模板资源行尾固定为 LF，摘要校验兼容 CRLF 检出，避免误报。
- Linux：补充 deb 包 maintainer 信息，恢复 AppImage/deb/rpm 打包。
- macOS：安装包上传只包含安装文件，避免 `.app` 目录导致 artifact 上传内存溢出。

### 说明

- 无数据库结构变化，升级兼容 1.0.0 / 1.0.1。
- 桌面安装包仍为未签名测试包：macOS 使用 ad-hoc 签名，Windows 未签名。

## [1.0.1] - 2026-11-04

修复 Windows 构建环境下的前端构建校验误报。

### 修复

- 构建工具包 LICENSE 通过 `.gitattributes` 固定以 LF 检出，避免 Windows 自动行尾转换；
- 前端构建依赖检查按规范化行尾读取许可正文，Windows 本地检出也能通过。

### 说明

- 无数据库结构变化，升级兼容 1.0.0。
- 桌面安装包仍为未签名测试包：macOS 使用 ad-hoc 签名，Windows 未签名。

## [1.0.0] - 2026-11-04

首个开源版本，基于内部 1.0.0 源码基线整理发布。

### 新增

- 带外管理服务端：.NET 10 四层后端（API / Application / Domain / Infrastructure），支持 HP iLO4 与 Dell iDRAC 8 的只读采集与受控操作。
- PC 管理端：React 19 + Umi Max + Ant Design 6，包含运行总览、服务器管理、虚拟控制台、历史趋势、用户与权限、邮件与运行报表、MCP 配置与调用日志。
- 桌面版：Electron + 自包含 .NET 宿主 + SQLite，可在管理电脑本地独立运行。
- MCP 服务：OAuth 授权码 + PKCE，十个只读查询工具，调用方/工具/设备/网络白名单与调用审计。
- 部署示例：Dockerfile、Compose（本机与通用镜像仓库）、Nginx 配置、systemd 宿主发布模板。
- 检查链：`scripts/doctor.mjs`、`scripts/bootstrap.mjs`、`scripts/verify.mjs`、`scripts/check-docs.mjs`、`scripts/oob-contracts.mjs` 与 `.github/workflows/ci.yml`。

### 文档

- README、架构、开发手册、接口合同（REST 与 MCP）、ADR、数据库 ER、版本与升级规约、使用指南（简中/繁中/英文）。
- 开源标准文件：LICENSE（Apache-2.0）、NOTICE、SECURITY、CONTRIBUTING、AGENTS、CHANGELOG 与 GitHub Issue/PR 模板。

### 说明

- 真实电源控制默认关闭；虚拟控制台与虚拟介质能力依赖固件、设备与浏览器环境，详见文档。
- README 截图使用合成演示数据。
- 本版本不含内部变更记录、内部部署地址、真实设备信息与任何凭据。

# 部署资源

本目录保存带外管理系统的容器与宿主部署示例。所有示例均使用占位域名、文档网段与 `CHANGE_ME` 凭据，部署前须按目标环境填写。

| 入口 | 用途 |
| --- | --- |
| [oob/README.md](oob/README.md) | 本机 PostgreSQL 17 / TimescaleDB 开发测试实例与完整系统 Compose 示例 |
| [oob/PODMAN.md](oob/PODMAN.md) | 使用 Podman/Docker 手动构建与启停 API、PC、数据库三个容器 |
| [oob/HOST-SYSTEMD.md](oob/HOST-SYSTEMD.md) | 宿主部署通用模板：Docker 数据库 + systemd 托管 .NET 应用 + Kestrel HTTPS |
| [oob/harbor/README.md](oob/harbor/README.md) | 使用通用镜像仓库的三服务 Compose 示例（API、PC、PostgreSQL/TimescaleDB） |

## 通用要求

- **凭据注入**：数据库密码、BMC 账号、SMTP 口令、OAuth 证书与私钥只能通过环境变量、Docker/Kubernetes secrets 或私有目录挂载提供；不要写入 Compose 文件、镜像、源码或公开文档。
- **私有目录**：示例使用 `.local/oob/` 作为本机私有配置目录（已在 `.gitignore` 中忽略），部署者可按自己的目录结构调整挂载路径。
- **网络与证书**：示例中的主机名、IP 与证书路径均为占位值；真实部署需自行完成 DNS、TLS 证书、防火墙与反向代理配置。
- **验证边界**：资源准备、镜像构建、数据库结构、账号初始化与服务运行分别验证；配置文件存在不等于服务已就绪。

仓库不包含生产部署记录、真实镜像仓库地址或任何环境凭据。安全注意事项见 [SECURITY.md](../SECURITY.md)。

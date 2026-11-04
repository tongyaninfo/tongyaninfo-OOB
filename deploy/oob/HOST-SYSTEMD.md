# 宿主 systemd 部署模板

本模板描述一种单机部署方式：PC 构建产物由后端 `wwwroot` 托管，.NET 应用以 framework-dependent 方式发布并由 systemd 管理，PostgreSQL 17/TimescaleDB 运行在 Docker 容器中并使用宿主目录持久化。它不是唯一部署方式；容器化部署见 [PODMAN.md](PODMAN.md) 与 [harbor/README.md](harbor/README.md)。

## 占位约定

| 占位符 | 含义 | 示例 |
| --- | --- | --- |
| `<OOB_HOST>` | 对外访问的主机名或 IP | `oob.example.com` |
| `<OOB_USER>` | 运行应用的专用系统账号 | `oob`（禁止使用 root 运行应用） |
| `<PUBLISH_DIR>` | 应用发布目录 | `/opt/oob-management/app` |
| `<TLS_DIR>` | 证书与私钥目录（仅运行账号可读） | `/etc/oob-management/tls` |
| `<DATA_DIR>` | 数据库与应用持久数据目录 | `/var/lib/oob-management` |
| `<DB_PORT>` | 数据库宿主端口（建议仅回环） | `55432` |

下文命令中的示例值必须按目标环境替换；不要把真实主机名、证书路径或凭据提交到仓库。

## 先决条件

- Linux 宿主（x86_64/arm64 均可，按实际架构选择发布目标），已安装 Docker。
- .NET SDK 10.0.300（构建机）与 .NET 10 ASP.NET Core Runtime（宿主）。
- 已签发的 TLS 证书与私钥（PEM/PFX 均可，按实际部署方式配置 Kestrel）。
- 专用运行账号 `<OOB_USER>` 与目录权限已准备。

## 1. 数据库容器

使用 [compose.yaml](compose.yaml)（固定镜像 `timescale/timescaledb:2.31.0-pg17` 或自行核验的等价镜像），数据目录绑定到宿主持久目录并限制访问：

```sh
install -d -m 700 <DATA_DIR>/postgres <DATA_DIR>/pgdata
export OOB_POSTGRES_IMAGE=timescale/timescaledb:2.31.0-pg17
docker compose -f deploy/oob/compose.yaml up -d
docker compose -f deploy/oob/compose.yaml ps
```

首次部署后按 [deploy/oob/README.md](README.md) 创建数据库与账号、启用 TimescaleDB 扩展，并由后端显式命令完成迁移与管理员初始化。普通启动不自动建库或迁库。

## 2. 发布应用

在构建机（或 CI）执行：

```sh
# 后端 framework-dependent 发布
dotnet publish backend/src/OobManagement.Api/OobManagement.Api.csproj \
  -c Release -r linux-x64 --self-contained false -o <PUBLISH_DIR>
# PC 构建并放入 wwwroot
npm --prefix frontend/PC ci --ignore-scripts --no-audit --no-fund
node frontend/PC/node_modules/@umijs/max/bin/max.js setup
npm --prefix frontend/PC run build
cp -r frontend/PC/dist/. <PUBLISH_DIR>/wwwroot/
```

发布目录应包含 `OobManagement.Api.dll`、依赖清单、`VERSION`、`releases/*.json` 与 `wwwroot/`。构建与发布不得夹带 `.local/`、开发 `dist-dev/`、测试夹具、密钥或 `.env` 文件；发布前检查一次。

```sh
install -d -m 750 -o <OOB_USER> -g <OOB_USER> <PUBLISH_DIR> <DATA_DIR>/app-data
```

## 3. HTTPS 与证书

Kestrel 直接提供 TLS 时，在私有配置（不要写入源码或公开示例）中设置证书：

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:443",
        "Certificate": {
          "Path": "<TLS_DIR>/server.crt",
          "KeyPath": "<TLS_DIR>/server.key"
        }
      }
    }
  }
}
```

要求：

- 证书目录仅 `<OOB_USER>` 可读，私钥权限 `640` 或更严格。
- `AllowedHosts` 必须与实际访问主机名一致，禁止使用通配。
- 如由反向代理终止 TLS，请保留真实 `Host`/协议并只信任明确的代理来源；不要用关闭认证或放宽任意 Origin 解决 WebSocket 握手问题。
- 80 端口如需要可仅做跳转到 HTTPS。

## 4. systemd unit 示例

`/etc/systemd/system/oob-management.service`：

```ini
[Unit]
Description=OOB Management API
After=network-online.target docker.service
Wants=network-online.target
Requires=docker.service

[Service]
Type=simple
User=<OOB_USER>
Group=<OOB_USER>
WorkingDirectory=<PUBLISH_DIR>
ExecStart=/usr/bin/dotnet <PUBLISH_DIR>/OobManagement.Api.dll --urls https://0.0.0.0:443
Restart=on-failure
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=AllowedHosts=<OOB_HOST>
Environment=Oob__PrivateDirectory=<DATA_DIR>/private
Environment=Oob__ReadOnlyKeys=true
Environment=Database__Host=127.0.0.1
Environment=Database__Port=<DB_PORT>
Environment=XDG_DATA_HOME=<DATA_DIR>/app-data
LimitNOFILE=65536
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
```

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now oob-management.service
sudo systemctl status oob-management.service
```

服务启动前由部署者确认数据库容器健康、迁移已完成、私有配置与密钥目录已就位；缺失时应用应报告未就绪，而不是自动建库或生成密钥。

## 5. 防火墙

示例使用文档网段；实际规则按真实访问来源收紧：

```sh
# 仅允许管理网段访问 443（示例网段，请替换为真实管理网段）
sudo firewall-cmd --permanent --add-rich-rule='rule family="ipv4" source address="192.0.2.0/24" port port="443" protocol="tcp" accept'
sudo firewall-cmd --reload
```

数据库端口不建议对外开放；如需运维访问，限制为回环或明确的管理主机。

## 6. 验证

```sh
curl --fail --silent --show-error https://<OOB_HOST>/health
curl --fail --silent --show-error https://<OOB_HOST>/ | head
systemctl status oob-management.service
docker compose -f deploy/oob/compose.yaml ps
```

`/health` 只证明进程响应；还应验证登录、数据库读取、WebSocket 推送、页面深链接（刷新非根路径）与 TLS 证书链。实际浏览器、设备与 MCP/SMTP 验收单独记录。

## 7. 升级与回退

1. 先备份数据库、私有配置、密钥目录与发布目录（或保留上一版本发布目录）。
2. 停止应用服务；按 [版本规约](../../docs/releases/README.md) 执行新增迁移。
3. 替换发布目录内容，启动服务并完成第 6 节验证。
4. 回退前先证明旧程序与新结构兼容；不能证明时按备份恢复数据库、密钥/配置与旧制品，不默认执行 EF `Down`。

本模板不授予任何具体目标的数据库结构变更、删除、部署或秘密修改权限；这些操作须按所在环境的流程单独确认。全流程不得把真实凭据、内网地址或证书写入仓库与公开文档。

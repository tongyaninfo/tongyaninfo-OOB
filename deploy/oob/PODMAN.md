# Podman / Docker 完整系统手动部署

本说明部署 API、PC 与 PostgreSQL/TimescaleDB 三个容器，适合本机验证或小规模单机部署。浏览器访问 `http://127.0.0.1:8000`，API 为 `http://127.0.0.1:5080`。示例端口只绑定本机回环；远程访问、HTTPS 与正式发布须另行配置。

> 开源版本不包含内部编排脚本（`scripts/oob-podman.mjs` 等），以下命令使用标准 `podman` / `docker` 与 `compose` 手动执行。

## 构建镜像

构建上下文只包含发布产物和公开配置，不包含源码树、`.local/`、`.env`、认证文件或密钥。

```sh
# API：先发布后端（framework-dependent）
dotnet publish backend/src/OobManagement.Api/OobManagement.Api.csproj -c Release -o .build/api
# PC：先构建前端产物
npm --prefix frontend/PC run build
mkdir -p .build/pc && cp -r frontend/PC/dist/. .build/pc/
# 构建镜像
podman build -f deploy/oob/api.Containerfile -t localhost/oob-management-api:local .build
podman build -f deploy/oob/pc.Containerfile -t localhost/oob-management-pc:local .
```

`api.Containerfile` 与 `pc.Containerfile` 使用可覆盖的基础镜像参数（`API_BASE`、`PC_BASE`），离线环境应先自行导入固定摘要的基础镜像。

## 准备私有配置

在 `.local/oob/`（已被 `.gitignore` 忽略）准备以下文件，权限 600，目录 700：

| 文件 | 用途 |
| --- | --- |
| `application.json` | Bootstrap 管理员/设备种子及运行开关 |
| `database-connections.json` | Development/Testing 的 Npgsql 连接 |
| `keys/` | 设备凭据与会话保护的持久化密钥 |

数据库容器使用 `compose.yaml`，应用容器使用 `compose.app.yaml`。两者通过同一网络与数据卷协作；数据库 compose 的 `secrets` 需要 `postgres-password.txt`。

```sh
install -d -m 700 .local/oob
printf '%s\n' 'CHANGE_ME_BOOTSTRAP_PASSWORD' > .local/oob/postgres-password.txt
chmod 600 .local/oob/postgres-password.txt
```

## 启动与停止

```sh
# 数据库
export OOB_POSTGRES_IMAGE=timescale/timescaledb:2.31.0-pg17
podman compose -f deploy/oob/compose.yaml up -d
# 应用（复用同一网络，需先设置运行数据目录）
export OOB_RUNTIME_DIRECTORY="$PWD/.local/oob/runtime"
mkdir -p "$OOB_RUNTIME_DIRECTORY"
podman compose -f deploy/oob/compose.yaml -f deploy/oob/compose.app.yaml up -d
```

三个容器均设置 `restart: "no"`，宿主机重启后由使用者显式启动。停止并保留数据：

```sh
podman compose -f deploy/oob/compose.yaml -f deploy/oob/compose.app.yaml stop
```

停止后不再采样，数据卷、账号、配置与历史保留；不执行服务器电源动作。

| 容器 | 职责 |
| --- | --- |
| `oob-management-postgres` | 数据库与 TimescaleDB |
| `oob-management-api` | 认证、HTTP、WebSocket 与后台采集 |
| `oob-management-pc` | PC 静态资源、API 与 WebSocket 同源代理 |

## 配置与持久化

- `application.json`、`database-connections.json` 与 `keys/` 以只读方式挂载；`Oob:PrivateDirectory` 指定挂载目录，`Database:Host/Port` 只覆盖容器数据库地址，不改变库名、账号或密码。容器显式启用 `Oob:ReadOnlyKeys` 关闭自动生成；密钥轮换须另行准备，不能删除密钥解决启动问题。
- 异常缓冲与虚拟介质目录映射到 `OOB_RUNTIME_DIRECTORY` 指定的持久目录；首次部署确定路径后，已有容器不会随环境变量自动重建。
- 普通启动不升级数据库结构、不导入设备、不重置账号。真实电源控制沿用配置开关，默认关闭。
- Nginx 保留原 Host/端口并转发 WebSocket；页面深链接刷新回到 PC 入口，不关闭来源校验。虚拟介质公网地址由 `VirtualMedia:PublicBaseUrl` 配置，不能使用 BMC 不可达的回环地址。

## 验证与回退

```sh
curl --fail --silent http://127.0.0.1:5080/health
curl --fail --silent http://127.0.0.1:8000/health
```

`/health` 只证明进程能响应；应同时检查登录、数据库读取、WebSocket 推送与页面深链接。核对三个容器状态与端口监听，再次启动后确认原账号、服务器与历史仍可读取。

回退到本机调试：先 `stop` 停止应用容器，再单独启动数据库容器，按[开发手册](../../docs/development.md)启动本机 API/PC。发布更新需显式替换容器；本仓库不提供删除、强制重建、清空卷或自动回滚数据库的入口。

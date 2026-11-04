# 通用镜像仓库三服务 Compose

[compose.yaml](compose.yaml) 组合 API、PC 与 PostgreSQL/TimescaleDB 三个镜像，目标平台为 `linux/amd64`。它是独立部署入口，不合并原本机 Compose，也不复用其固定容器名、数据卷或私有配置。

本示例使用占位仓库 `registry.example.com`。实际使用时替换为你的镜像仓库（Harbor、GitLab Registry、Docker Registry 等），并确保三个镜像已构建并推送。镜像构建参见 [Podman 说明](../PODMAN.md) 与根目录 `deploy/oob/` 下的 Containerfile。

| 运行服务 | 镜像示例 | 用途 |
| --- | --- | --- |
| `postgres` | `registry.example.com/outofband/oob-management-postgres:1.0` | PostgreSQL 17.11 + TimescaleDB 2.31.0 |
| `api` | `registry.example.com/outofband/oob-management-api:1.0` | HTTP、认证、采集、WebSocket 与 MCP/OAuth |
| `pc` | `registry.example.com/outofband/oob-management-pc:1.0` | PC 页面与同源代理，默认宿主 `8000` 端口 |

上表为示例仓库与标签。实际部署时通过 `OOB_REGISTRY`、`OOB_IMAGE_TAG` 指定仓库和版本，本栈后续命令保持一致。镜像仓库地址不等于应用的 MCP 公开地址。

数据库和 API 不发布宿主端口。数据库默认使用 `postgres-data` 命名卷，可通过下文参数改为绑定指定宿主目录；API 异常缓冲及介质文件使用独立的 `application-data` 命名卷。配置、密码文件和密钥在部署者指定的私有目录。默认项目名为 `outofband-harbor`，三个运行服务均为手动启停。

## 准备配置

需要 Docker Engine 与支持 Compose Specification 的 Docker Compose 插件；也可使用已配置 Compose provider 的 Podman。下文从本目录执行；Podman 用户将 `docker compose` 换成 `podman compose`。仅复制本目录也可交付，保留其中的初始化脚本和示例文件。

以**新部署的独立目录**为例，先准备文件，不覆盖已有部署配置：

```sh
OOB_DEPLOY_HOME=/srv/outofband
install -d -m 700 "$OOB_DEPLOY_HOME/private/keys"
cp -n settings.env.example "$OOB_DEPLOY_HOME/deploy.env"
for file in application.json database-connections.json postgres-password.txt application-database-password.txt admin-password.txt; do
  cp -n "$file.example" "$OOB_DEPLOY_HOME/private/$file"
done
chmod 600 "$OOB_DEPLOY_HOME/deploy.env" "$OOB_DEPLOY_HOME/private/"*.json "$OOB_DEPLOY_HOME/private/"*.txt
```

由部署者编辑这些私有文件，把全部 `CHANGE_...` 占位符替换为实际值：

| 文件或选项 | 填写要求 |
| --- | --- |
| `deploy.env` 的 `OOB_PRIVATE_DIRECTORY` | 上述私有目录的绝对路径，例如 `/srv/outofband/private` |
| `OOB_POSTGRES_DATA_DIRECTORY` | 默认留空，使用 Docker 管理的命名卷；指定 Linux 宿主绝对路径时绑定该目录，实际数据库数据位于其 `pgdata/` 子目录，详见下一节 |
| `OOB_REGISTRY`、`OOB_IMAGE_TAG` | 默认 `registry.example.com:443`、`1.0`；仓库地址不带协议或 `/outofband`，更换地址时先确认三个镜像均已复制到该仓库 |
| `OOB_ALLOWED_HOSTS` | 浏览器访问的主机名或 IP；不带协议和端口；多个值用分号分隔，保留 `localhost;127.0.0.1` 供检查 |
| `OOB_HTTP_BIND`、`OOB_HTTP_PORT` | 默认 `0.0.0.0:8000`；只允许本机访问时改成 `127.0.0.1` |
| `OOB_WORKER_ID` | 同一发号域内唯一的实例编号，单实例默认 `1`；不能直接扩成多个 API 副本共享介质目录 |
| `postgres-password.txt` | 独立引导账号 `oob_bootstrap` 的密码，不用 Harbor 密码 |
| `application-database-password.txt` | 非超级用户 `oob_app` 的数据库密码 |
| `database-connections.json` | `Development` 项中填相同的应用数据库密码；特殊分号、双引号按 Npgsql 连接字符串规则转义 |
| `admin-password.txt` | 新空库首个系统账号 `admin` 的初始密码，使用至少 8 个字符的独立密码 |
| `application.json` | 示例仅保留空的设备种子；按需添加业务配置，不放上述 Compose 已管理的同名选项 |

密码文件各放一行，不设置空密码。`deploy.env` 仅保存非秘密选项，不能提交私有目录。API 在 `Production` 环境仍读取连接文件的 **`Development`** 项；不要改成未被当前实现读取的 `Production` 项。

当前 API 最后加载 `application.json`，其同名选项会覆盖 Compose 环境变量。勿把 `Urls`、`AllowedHosts`、`Oob`、`Database`、`Collector`、`PowerControl` 或 `Bootstrap.AdminPassword` 重复写入该文件；需要扩展配置时先核对实际优先级。已有系统迁移时备份并配套恢复原数据库、密钥及运行数据，不能用空密钥目录替代。

## 选择数据库存储与运行参数

首次部署时先确定一种数据库存储方式，并在本栈后续全部 Compose 命令中保持一致：

| `OOB_POSTGRES_DATA_DIRECTORY` 的值 | 数据保存位置 |
| --- | --- |
| 未设置或空值 | 使用原 `postgres-data` 命名卷，由 Docker 管理；默认项目的实际卷名为 `outofband-harbor_postgres-data`，不会额外导出到用户指定的宿主目录 |
| `/srv/outofband/postgres` 等绝对路径 | 将该宿主目录绑定到容器 `/var/lib/postgresql/data`，实际 `PGDATA` 为 `/srv/outofband/postgres/pgdata` |

宿主目录只使用 **Linux 绝对路径，不能含冒号，不用相对路径或 `~`**。路径属于容器引擎所在的宿主；远程 Docker 或 Podman machine 场景下，它属于远程主机或 VM，不一定是运行命令的电脑。macOS/Windows 使用的目录还须满足引擎的目录共享及访问权限要求。

部署者先准备目录及 PostgreSQL 运行身份所需的写入权限；新集群使用空目录，复用已有数据则须确认是兼容的 PostgreSQL 17 集群，并按 `pgdata/` 子目录布局放置。参数只决定挂载位置，不自动复制、导出或迁移已有数据。对已运行的栈，应继续使用原存储选择；改变挂载位置前单独完成备份、恢复和迁移方案，不能把下面三种示例依次套到同一个已有栈。

数据库挂载带 `Z` 选项，使启用 SELinux 的 Linux 引擎为该专用目录设置容器私有标签；不要将其他应用共享的目录作为数据库数据目录。目录属主与文件写入权限仍需正确设置，不要用 `chmod 777` 代替权限配置。

参数有三种传递方式，**任选一种**。以下非秘密示例同时展示端口、访问主机、镜像仓库及版本；需在初始化前按目标环境选定。

### 方式一：写入部署配置文件（推荐）

编辑准备好的 `$OOB_DEPLOY_HOME/deploy.env`，默认使用命名卷时保留：

```dotenv
OOB_POSTGRES_DATA_DIRECTORY=
```

若本次新部署选择宿主绑定目录，则在同一文件中改为下面这组示例值。`OOB_PRIVATE_DIRECTORY` 与之前准备的私有目录一致，访问地址示例为 `http://oob.example.com:8080`：

```dotenv
OOB_PRIVATE_DIRECTORY=/srv/outofband/private
OOB_POSTGRES_DATA_DIRECTORY=/srv/outofband/postgres
OOB_REGISTRY=registry.example.com:443
OOB_IMAGE_TAG=1.0
OOB_ALLOWED_HOSTS=oob.example.com;localhost;127.0.0.1
OOB_HTTP_BIND=0.0.0.0
OOB_HTTP_PORT=8080
```

后续每条命令都显式带上该文件，本手册的拉取、初始化、密钥准备及启停步骤已使用这一写法：

```sh
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml config --quiet
```

### 方式二：在当前终端持久导出

若由终端环境管理这些选项，可在同一个终端先设置：

```sh
export OOB_POSTGRES_DATA_DIRECTORY=/srv/outofband/postgres
export OOB_REGISTRY=registry.example.com:443
export OOB_IMAGE_TAG=1.0
export OOB_ALLOWED_HOSTS='oob.example.com;localhost;127.0.0.1'
export OOB_HTTP_BIND=0.0.0.0
export OOB_HTTP_PORT=8080
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml config --quiet
```

这些值覆盖 `deploy.env` 的同名值，当前终端后续命令会继承。新开终端后，须重新导出相同值，或改用保存了相同值的部署配置文件；其余必填选项仍由 `deploy.env` 提供。若本次选择默认命名卷，应使用空的 `OOB_POSTGRES_DATA_DIRECTORY`。

### 方式三：只给一条命令传参

命令前的赋值只影响紧随其后的那一条命令：

```sh
OOB_POSTGRES_DATA_DIRECTORY=/srv/outofband/postgres \
OOB_REGISTRY=registry.example.com:443 OOB_IMAGE_TAG=1.0 \
OOB_ALLOWED_HOSTS='oob.example.com;localhost;127.0.0.1' \
OOB_HTTP_BIND=0.0.0.0 OOB_HTTP_PORT=8080 \
  docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml config --quiet
```

采用这种方式，必须在本栈**所有后续 Compose 命令**前重复相同参数，包括镜像拉取、启动 PostgreSQL、`db-init`、`api-init`、密钥准备、最终 `up`、检查和停止；密钥准备的 `OOB_KEYS_READ_ONLY=false OOB_COLLECTOR_ENABLED=false` 额外前缀也应同时保留。仅给第一次命令传宿主目录、后面漏传，会按文件或默认值切回命名卷。日常操作较多时，优先使用方式一。

完成下文的数据库初始化和密钥准备后，也可直接传参启动。下面的其他必填参数来自 `deploy.env`，临时指定数据库目录及页面端口：

```sh
OOB_POSTGRES_DATA_DIRECTORY=/srv/outofband/postgres OOB_HTTP_PORT=8080 \
  docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml up -d
```

同名变量的优先级为：**命令前一次性赋值 > 已 `export` 的环境值 > `--env-file` 文件值 > Compose 默认值**。空环境值也会覆盖文件中的非空值，例如 `OOB_POSTGRES_DATA_DIRECTORY= docker compose ...` 会恢复使用命名卷；因此不能用空值来表示“继续沿用文件中的宿主目录”。这里是 Compose 参数优先级，API 的 `application.json` 加载顺序另按上一节处理。

## 拉取镜像

```sh
export OOB_REGISTRY=registry.example.com
export OOB_IMAGE_TAG=1.0
docker login "$OOB_REGISTRY"
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml config --quiet
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml pull postgres api pc
```

`compose pull` 按最终参数拉取三个运行镜像。也可逐个拉取；自定义 `OOB_REGISTRY` 或 `OOB_IMAGE_TAG` 时同步替换地址与版本：

```sh
docker pull --platform linux/amd64 registry.example.com/outofband/oob-management-postgres:1.0
docker pull --platform linux/amd64 registry.example.com/outofband/oob-management-api:1.0
docker pull --platform linux/amd64 registry.example.com/outofband/oob-management-pc:1.0
```

更换仓库时，`docker login` 也使用相同的目标地址；拉取完成后继续下文对应的初始化或日常启动流程。

登录密码交互输入。私有仓库常见证书与认证域名问题（证书过期、DNS 无法解析）需要在目标环境自行核对；Compose 不修改系统或 Docker 全局信任设置，不要用跳过 TLS 校验作为长期方案。

## MCP 容器入口与私有材料

PC 镜像将 `/mcp`、`/connect/*`、`/.well-known/*` 独立代理给 API，关闭请求/响应缓冲及这些协议路径的 Nginx access log；`/swagger*`、`/openapi*` 明确返回 404，不回退为 PC HTML。普通页面和 `/api/` 保持原入口，MCP 默认仍关闭。

Nginx 使用本跳实际地址和协议覆写 `X-Forwarded-For/Proto/Host`，不采信请求自行提供的转发链。三服务 Compose 默认只提供 HTTP；启用标准 OAuth/MCP 前，必须明确 HTTPS 在何处终止、API 信任哪些代理、实际 Host/协议/客户端 IP 如何验证。当前应用转发限制为一跳，外层 TLS 代理再经 PC 到 API 的两跳拓扑不会自动接续真实协议与源地址；不能仅增加一个公开 URL 或透传任意转发头就认为已完成 HTTPS 接入。

现有挂载只有两份 JSON、Data Protection `keys/` 与运行数据卷，不自动带入 OAuth 证书。启用前，在部署覆盖配置中为 API 增加 OAuth 证书目录的只读 bind mount，并将 `Mcp.SigningCertificatePath`、`Mcp.EncryptionCertificatePath` 设为对应的**容器内绝对路径**，例如 `/run/oob/oauth/signing.pfx`、`/run/oob/oauth/encryption.pfx`；这两个路径仅是示例，不创建真实目录、证书或秘密。宿主源目录与容器目标路径分别核对，PFX 需含有效 RSA 私钥。不要把证书复制进镜像，也不要用重新生成替代恢复原材料。

Compose 的 `XDG_DATA_HOME=/var/lib/oob` 与 `application-data:/var/lib/oob/OutOfBandManagement` 已覆盖 `mcp-audit/Production` 缓冲，继续配套备份该卷。服务开关、调用方、OAuth/PKCE、白名单及日志限额见[MCP使用说明](../../../docs/api/mcp/README.md)；代理接通不等于服务已启用或目标客户端已验收。

## 已有库升级

已有库先以实际应用连接身份核对数据库、有效 SearchPath/schema、完整待执行列表与备份，不只查询默认 public 后推断应用状态。当前需分别核验业务 `__EFMigrationsHistory` 和 OAuth `__McpOAuthMigrationsHistory`：业务链应包含 `20261102130630_AddMcpManagement`，OAuth 链应包含 `20261102130635_AddMcpOAuthStorage`，并确认此前权限、邮件及报表迁移没有遗漏。

按交付时审查并明确目标的纯 EF 迁移工具，串行应用业务 OobDbContext、OAuth McpOAuthDbContext 两条链；该操作与镜像拉取、普通启动分别执行。前后核对既有列的数据行数与摘要，合法新增字段单独核验。`api-init`/`--initialize` 除迁移还包含 Bootstrap 账号及服务器处理，不能作为仅迁移已有库的无副作用命令直接复用；`db-init` 也会调整数据库权限等状态，不是已有库的只读预检。

## 新空库的显式初始化

这些命令会创建新数据库集群、应用角色、数据库、扩展、业务表及首个管理员。应在对应新环境及结构初始化已获确认后，由部署者执行；本文不是对现有环境执行迁库的授权，也不表示任意目标环境已完成这些操作。

先启动数据库，再逐个运行两个可审阅的初始化任务，确认上一项退出成功后才继续下一项；不要执行 `--profile initialize up`，避免两个初始化任务并行运行：

```sh
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml up -d postgres
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml --profile initialize run db-init
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml --profile initialize run api-init
```

`db-init` 从密码文件读取引导和应用密码，创建缺失的非超级应用角色及 `oob_management` 库，在该库安装 TimescaleDB 2.31.0，并核验应用身份可以认证。已有角色不重设密码、已有库不重建，但仍会调整连接权限、public schema 权限、时区和遥测设置，因此不是只读预检；密码不符或扩展版本不符会失败，需要核对已有对象和升级方案。数据库镜像只带引擎与扩展软件包，不带现有业务数据。

`api-init` 用应用身份执行镜像中现有的 `--initialize`，应用正式 EF 迁移。只有用户表完全为空时创建首个 `admin`；已有账号不会重设密码。示例的 `Bootstrap.Servers=[]` 防止导入设备配置。两个任务受 `initialize` profile 控制，普通 `up` 不运行它们；任务容器保留，不自动删除。

API 的 `--initialize` **不保证生成 Data Protection 密钥**。新实例需再明确完成一次新密钥准备；已有系统直接使用配套恢复的原密钥，跳过下一节。

## 新实例的密钥准备

仅对新实例，明确允许在私有 `keys/` 中生成新密钥后执行。配置文件不得覆盖这里的采集和密钥选项。先临时允许密钥写入并关闭采集，启动 API/PC，再通过真实 GET 请求触发令牌保护；请求结果不输出：

```sh
OOB_KEYS_READ_ONLY=false OOB_COLLECTOR_ENABLED=false \
  docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml up -d api pc
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml \
  exec pc wget --quiet -O /dev/null http://127.0.0.1:8080/api/auth/csrf
find "$OOB_DEPLOY_HOME/private/keys" -maxdepth 1 -name 'key-*.xml' -print
```

等待 PC 健康检查通过后再请求 CSRF；请求必须成功且密钥文件确实存在，不能仅凭 `/health` 判断成功。上面的 `find` 使用示例私有目录；如自定义了 `OOB_PRIVATE_DIRECTORY`，应检查该实际目录的 `keys/`。密钥文件与目录限部署账号访问，不打印内容。完成后先停止这两个临时可写运行实例，再恢复 `deploy.env` 中默认的只读配置启动：

```sh
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml stop pc api
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml up -d api pc
```

日常 `OOB_KEYS_READ_ONLY=true` 同时禁止 API 自动生成密钥并只读挂载目录；只读模式的密钥到期或轮换另行维护。不能用重新生成密钥解决旧设备密码无法解密。

## 日常启动、检查与停止

只有应用库已按目标镜像版本完成初始化/升级、私有配置与密钥就绪时启动整个系统：

```sh
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml up -d
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml ps
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml \
  exec pc wget --quiet -O /dev/null http://127.0.0.1:8080/api/auth/csrf
```

浏览器访问部署主机的 `8000` 端口，或配置的其他端口。PC 代理保留浏览器 Host，API 因而需要匹配的 `AllowedHosts`；API 服务名必须保持为 `api`。本入口提供 HTTP，正式 HTTPS 需按已确认目标配置反向代理及来源处理，不能直接当作原 systemd HTTPS 方案的替换。

数据库健康检查通过 TCP 认证验证 PostgreSQL 17、UTC、TimescaleDB 可用版本与 preload；它不检查业务迁移。PC `/health` 仅验证 API 进程与代理。上线前仍须按“已有库升级”核对业务与 OAuth 两条历史，再检查 CSRF、真实登录、服务器列表、历史和 WebSocket；如需 MCP，另验 TLS、授权和目标客户端。旧库不能凭容器健康就直接升级上线。电源默认关闭，真机动作须按部署环境的流程单独确认。

默认后台采集为开启；现场联调阶段可将 `OOB_COLLECTOR_ENABLED=false` 后重建 API 配置。停止保留数据库、配置、密钥和历史数据：

```sh
docker compose --env-file "$OOB_DEPLOY_HOME/deploy.env" -f compose.yaml stop pc api postgres
```

更新前备份数据库、私有配置、密钥及 `application-data`，核对镜像摘要和正式迁移方案。不要对现有部署重复套用空库初始化，也不提供删除卷、清库或自动回滚命令。镜像推送、环境迁移、应用部署与人工业务验收分别记录，不从本文推断某环境已上线。

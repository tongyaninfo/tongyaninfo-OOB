# 带外管理数据库与容器部署

本目录提供本项目 PostgreSQL 17 / TimescaleDB 开发测试实例与完整系统容器部署的示例配置，不复用其他项目的数据库、容器、卷或代理设置。

- 完整 PC/API 容器构建与手动启停见 [Podman 运行说明](PODMAN.md)；
- 宿主部署（Kestrel HTTPS + systemd）见[宿主发布模板](HOST-SYSTEMD.md)；
- 使用通用镜像仓库的三服务 Compose 见 [harbor/README.md](harbor/README.md)。

> 开源版本不包含内部数据库编排脚本（如 `scripts/oob-database.mjs`、`scripts/oob-podman.mjs`）。下文命令均使用标准 `docker compose` / `podman compose` 手动执行，私有配置文件需由部署者自行准备。

## 本机实例约定

| 对象 | 示例值 |
| --- | --- |
| Compose 项目 | `oob-management` |
| 容器 | `oob-management-postgres` |
| 主机入口 | `127.0.0.1:55432` |
| 数据卷 | `oob-management-postgres-data` |
| 开发数据库/账号 | `oob_management_dev` / `oob_dev` |
| 测试数据库/账号 | `oob_management_test` / `oob_test` |

## 准备私有配置

从仓库根执行，创建被忽略的私有目录（`.local/oob/` 已在 `.gitignore` 中）：

```sh
install -d -m 700 .local/oob
printf '%s\n' 'CHANGE_ME_BOOTSTRAP_PASSWORD' > .local/oob/postgres-password.txt
chmod 600 .local/oob/postgres-password.txt
```

按需创建 `application.json`（Bootstrap 管理员/设备种子与运行开关）、`database-connections.json`（Development/Testing 的 Npgsql 连接）与 `keys/`（设备凭据加密密钥）。`database-connections.json` 只放在私有目录，不打印、不提交、不复制到共享配置。

> 不要用示例中的占位值连接生产库；不要用其他项目的数据库或密钥补齐缺项。

## 启动数据库

选择已核验的 PostgreSQL/TimescaleDB 镜像（示例固定为 `timescale/timescaledb:2.31.0-pg17`）：

```sh
export OOB_POSTGRES_IMAGE=timescale/timescaledb:2.31.0-pg17
docker compose -f deploy/oob/compose.yaml up -d
docker compose -f deploy/oob/compose.yaml ps
```

首次启动后在数据库实例内创建开发/测试数据库、账号并启用 TimescaleDB 扩展。具体 SQL 与账号策略由部署者按环境执行；`compose.yaml` 通过 Docker secret 文件注入引导账号密码，不把密码写入公开 YAML 或命令行。

## 验证

```sh
docker compose -f deploy/oob/compose.yaml exec postgres pg_isready -U oob_bootstrap
docker compose -f deploy/oob/compose.yaml exec postgres psql -U oob_bootstrap -c "select version();"
docker compose -f deploy/oob/compose.yaml exec postgres psql -U oob_bootstrap -c "select extname from pg_extension where extname = 'timescaledb';"
```

开发与测试账号不能互连对方数据库。EF 迁移与管理员初始化由后端显式命令执行，应用普通启动不建库、不迁库。

```sh
docker compose -f deploy/oob/compose.yaml stop    # 停止但保留数据卷
docker compose -f deploy/oob/compose.yaml up -d   # 再次启动
```

不提供 `down -v`、删除或重建数据卷的隐式入口；恢复或删除须明确具体对象与影响。

## 镜像与网络

默认使用官方镜像 `timescale/timescaledb:2.31.0-pg17`；离线或受限网络可基于 [Dockerfile](Dockerfile) 与 [package-manifest.json](package-manifest.json) 中的固定软件包构建本地镜像。构建上下文只放经官方索引 SHA-256 核验的 `packages/*.deb`，不能包含私密文件。

官方来源：[TimescaleDB 镜像](https://github.com/timescale/timescaledb-docker)、[PostgreSQL 兼容矩阵](https://www.tigerdata.com/docs/get-started/timescaledb-supported-platforms)、[Timescale 软件包](https://packagecloud.io/timescale/timescaledb)。

TimescaleDB Community Edition 的使用遵循[官方许可](https://www.tigerdata.com/legal/licenses)（TSL）。默认不启用历史自动删除、压缩或连续聚合策略；数据保留、备份恢复与生产部署须按实际容量与业务要求另行设计。配置存在不等于数据库已运行，验证结果须如实记录。

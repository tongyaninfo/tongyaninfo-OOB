# Docker Hub 不可达时使用官方 PostgreSQL 基础镜像与官方 Timescale 软件包。
# 构建上下文只包含经过 SHA-256 核验的 packages/*.deb，不得包含私密配置。
ARG POSTGRES_BASE=public.ecr.aws/docker/library/postgres@sha256:8630835ad34e3e09007371ac2d8785bd941c3c1fccc02145e3af050b9e520659
FROM ${POSTGRES_BASE}

COPY packages/*.deb /tmp/oob-packages/

# 依赖仅安装到本项目数据库镜像，不修改宿主或其他容器。
RUN dpkg -i /tmp/oob-packages/*.deb

LABEL cn.tongyaninfo.project="oob-management"
LABEL cn.tongyaninfo.timescaledb.source="https://packagecloud.io/timescale/timescaledb"
LABEL cn.tongyaninfo.timescaledb.version="2.31.0"

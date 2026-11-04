#!/bin/sh
# 仅供用户显式运行的初始化任务；不挂入 postgres 的自动 initdb 目录。
set -eu

PGPASSWORD="$(cat /run/secrets/postgres_password)"
OOB_APPLICATION_DATABASE_PASSWORD="$(cat /run/secrets/application_database_password)"
case "$PGPASSWORD" in
  ""|CHANGE_*) echo '请先配置数据库引导账号密码文件' >&2; exit 1 ;;
esac
case "$OOB_APPLICATION_DATABASE_PASSWORD" in
  ""|CHANGE_*) echo '请先配置应用数据库账号密码文件' >&2; exit 1 ;;
esac
export PGPASSWORD OOB_APPLICATION_DATABASE_PASSWORD

# 密码只经环境和 psql 引用变量传入；固定对象名，不拼接未转义的 SQL。
# 已有账号不重设密码，已有库不重建；新部署与已有库升级需分别确认。
psql --no-psqlrc --no-password --set ON_ERROR_STOP=1 <<'SQL'
\getenv application_password OOB_APPLICATION_DATABASE_PASSWORD
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE',
              'oob_app', :'application_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'oob_app')
\gexec
SELECT format('CREATE DATABASE %I OWNER %I', 'oob_management', 'oob_app')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'oob_management')
\gexec
REVOKE CONNECT ON DATABASE oob_management FROM PUBLIC;
GRANT CONNECT ON DATABASE oob_management TO oob_app;
ALTER DATABASE oob_management SET timezone = 'UTC';
ALTER DATABASE oob_management SET timescaledb.telemetry_level = 'off';
\connect oob_management
CREATE EXTENSION IF NOT EXISTS timescaledb VERSION '2.31.0';
GRANT USAGE, CREATE ON SCHEMA public TO oob_app;
DO $oob$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb' AND extversion = '2.31.0') THEN
    RAISE EXCEPTION '已有 TimescaleDB 扩展版本不是 2.31.0，请单独确认升级方案';
  END IF;
END;
$oob$;
SQL

# 单独验证应用身份，避免已存在角色的旧密码被误当作初始化成功。
PGPASSWORD="$OOB_APPLICATION_DATABASE_PASSWORD" psql --host "$PGHOST" \
  --username oob_app --dbname oob_management --no-psqlrc --no-password \
  --set ON_ERROR_STOP=1 --tuples-only --no-align \
  --command "SELECT current_user = 'oob_app' AND NOT rolsuper FROM pg_roles WHERE rolname = current_user" \
  | grep -qx t
printf '应用账号、数据库与 TimescaleDB 扩展准备完成；尚未执行业务迁移。\n'

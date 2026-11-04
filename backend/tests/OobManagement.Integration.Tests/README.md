# HTTP 与真实数据库集成测试

从仓库根运行：

```sh
dotnet test backend/tests/OobManagement.Integration.Tests/OobManagement.Integration.Tests.csproj
```

前置条件是已经显式初始化的独立 Testing PostgreSQL 数据库，以及已启用的 Timescale 扩展和时序表。连接参数只从被忽略的 `.local/oob/database-connections.json` 的 `Testing` 项读取；夹具核对其与开发库分离且名称明确包含 test，不自动回退，不执行迁移、建库或 schema 清理。

宿主通过 WebApplicationFactory 使用真实路由、认证、防伪、服务、EF 和 PostgreSQL。全部 `IOutOfBandService` 注册替换为纯内存 fake，Testing 环境关闭后台采集，电源控制仅在该 fake 宿主开启。测试不会连接带外设备或对真机执行电源动作；Data Protection 使用进程内临时 provider，不修改开发环境密钥。

覆盖统一信封与 HTTP 状态、登录/CSRF/权限、服务器校验与凭据边界、缺测和采样持久化、历史空桶及单位、中文数据库注释、复合主键、Timescale 聚合，以及电源动作必填和字符串合同、名称确认、先记意图、幂等与未知结果不重发。人工核对仅改变审计状态，由原操作人明确确认，不增加设备调用；错误名称或其他操作人不能解除阻断。

夹具采用每次唯一的用户和服务器名称前缀，普通主键通过测试宿主的统一 ID 服务生成。清理仅按本次前缀、主键和用户标识删除自己创建的 DML 数据；不删文件，不 drop 数据库/schema，不碰开发或生产数据。环境或必需能力缺失即失败，不能把未运行写成通过；本次日志保存在仓库外。

服务器配置迁移测试使用派生 `ServerConfigurationApplicationFactory`，仅在测试 DbContext 增加当前服务器前缀的查询过滤，继续使用真实 Npgsql、HTTP及现有表。全集导出只面对本夹具的合成凭据，避免读取共享 Testing 库中其他夹具使用不同临时密钥保存的数据；不改变正式服务的全量导出行为，也不创建或修改数据库结构。跨宿主密钥迁移另在桌面测试的两个独立 SQLite 文件库中验证。

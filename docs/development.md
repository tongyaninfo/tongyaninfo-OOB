# 开发与接手

先读[架构地图](architecture.md)、[一期方案](spec/带外管理系统一期实施方案.md)和目标模块 README。当前工程为后端四层、独立 PC 与桌面宿主；文档只描述已实现能力，规划部分单独标注。

完整系统在 Podman 中运行与手动启停见 [Podman 运行说明](../deploy/oob/PODMAN.md)，沿用已有数据和私密配置；下文保留本机开发与初始化入口。

## 环境与依赖恢复

精确版本以实际清单为准：根 SDK 10.0.300（允许同 feature band 的 latestPatch，见 [global.json](../global.json)）；PC 工程要求 Node >= 22 并使用自身 `package-lock.json`；合同生成工具使用 `frontend/package.json` 的独立依赖。不静默安装全局工具。

从仓库根执行：

```sh
node scripts/doctor.mjs
node scripts/bootstrap.mjs
```

doctor 不调用 Git、不安装依赖、不访问数据库或设备。bootstrap 按锁恢复合同工具与独立 PC 依赖，使用 `--ignore-scripts` 后显式运行已安装的本地 Umi setup，并恢复后端与测试 NuGet；不修改全局环境、不启动服务、不迁库。

## 数据库与私密配置

本项目 Compose 示例为 [deploy/oob](../deploy/oob/README.md)，独立实例映射 `127.0.0.1:55432`，开发/测试建议分别使用 `oob_management_dev` 和 `oob_management_test`。PostgreSQL 17.11 + TimescaleDB 2.31.0 的实际启动状态须以 `docker compose ps` 与数据库健康检查核验。

> 开源版本不包含内部数据库编排脚本；请使用 `docker compose` 手动完成 prepare/up/initialize，并自行准备数据库账号与私有配置文件。

首次新环境的数据库初始化、后端迁移和私密配置准备，均由部署者按自己的环境执行；不要用本仓库示例中的占位值直接连接生产库。

| 本机文件 | 用途 |
| --- | --- |
| `.local/oob/database.json` | 本项目数据库账号、密码和镜像选择 |
| `.local/oob/database-connections.json` | Development/Testing 的 Npgsql 连接 |
| `.local/oob/application.json` | Bootstrap 管理员/服务器种子及运行开关 |
| `.local/oob/keys/` | 设备凭据与会话保护的持久化密钥 |

秘密只放本地私有配置，文件不输出到日志或前端；重复初始化不覆盖已有管理员密码。测试设备可自行准备，地址和凭据不写入仓库、文档或截图；不得用其他项目数据库或密钥补齐缺项。

后端显式初始化命令见[后端说明](../backend/README.md#配置启动与显式初始化)。普通启动不迁库；Testing 只初始化测试身份、不导入真实设备种子。

## 正常运行

分别在两个终端从仓库根执行：

```sh
dotnet run --project backend/src/OobManagement.Api/OobManagement.Api.csproj --no-launch-profile
npm --prefix frontend/PC run dev
```

访问 `http://127.0.0.1:8000`，API 为 `http://127.0.0.1:5080`，前端 `/api/` 经同源代理；Swagger/OpenAPI 服务与 UI 已退役。`/health` 仅为进程健康响应，数据库与设备就绪另验。

实时监控 Hub 为同源 `/api/oob/live`，前后端均只启用 WebSocket；本机 Umi 代理启用 `ws: true`，修改代理配置后需重启前端。部署反向代理也须转发 WebSocket Upgrade 并保留用于同源核对的真实 Host/协议，不能用关闭认证或放宽任意 Origin 解决握手问题。详情与总览不再定时轮询；首次进入加载已保存数据，断线后仅恢复后续推送，不自动补取。采集照常入库，用户仍可从历史页显式查询断线期间的数据。

登录使用本地初始化的 `admin` 账号。通研品牌登录页只展示已接入的账户密码认证，不以演示身份绕过认证。实际首页位于 `/oob/overview`，服务器管理位于 `/oob/servers` 和详情标签；模板演示页（dashboard/form/list/profile/result/table-list/chatbot）已从仓库移除。

普通服务启动会对已启用服务器进行只读采样；需要只运行离线合同或自动测试时使用对应入口，它们不启动采集。停止只针对自己启动且已核对 PID/命令的进程，数据库 `stop` 保留卷；不执行全局杀进程或清空环境。

服务器表单新增“忽略 HTTPS 证书错误”，新增默认勾选；取消使用正常证书链验证，显式指纹始终优先。开关按设备保存，适用于连接测试、库存和持续采样，不修改系统 TLS 信任。开发/测试库迁移须由部署者显式执行；不能仅替换程序就假设数据库已升级。

## 合同更新

服务端 MCP 默认关闭；系统管理员从 `/system/mcp` 配置调用方及白名单，从 `/system/mcp/logs` 查询审计。普通 HTTP 本机开发地址不能作为正式远程 MCP 的公开 URL，须在获准私有配置中提供 HTTPS 根地址、既有签名/加密证书及可信代理。MCP 业务和 OAuth 组件分别维护增量迁移，普通启动不执行；不要以读取配置、查看工具或协议发现为由初始化数据库或证书。完整属性、OAuth/PKCE 流程、工具限制及桌面边界见[MCP使用说明](api/mcp/README.md)。

```sh
node scripts/oob-contracts.mjs check
node scripts/oob-contracts.mjs generate
```

修改源为后端实际路由/DTO/JSON 序列化。独立工具通过 MVC ApiExplorer、EndpointDataSource 和 JsonSchemaExporter 导出到仓库外临时目录，用父前端锁定的 json-schema-to-typescript 16.0.0 生成 PC 类型；check 逐字比较 `docs/api/oob/routes.json`、`schemas.json` 和 `frontend/PC/src/services/oob/generated.ts`，generate 才更新。保留 components.schemas 类型兼容出口；不恢复 Swagger/OpenAPI。临时目录不自动删除，不调用业务宿主或 Git，不读取私密配置，不访问 DB/设备/SMTP；不得手改正式字段掩盖漂移。

## 按范围验证

```sh
node scripts/verify.mjs --scope docs
node scripts/verify.mjs --scope frontend
node scripts/verify.mjs --scope backend
node scripts/verify.mjs
```

| 范围 | 必需内容 |
| --- | --- |
| docs | Node、脚本语法、Markdown 路径与检查链 |
| frontend | docs + 合同检查 + PC 本地 setup、lint、test、build |
| backend | docs + 后端构建、驱动测试、真实 Testing 库 HTTP/EF 集成 + 合同 |
| all（默认） | 两端与合同的完整必需检查；缺环境时如实记录跳过原因，不能记为通过 |

非文档范围都需要 SDK 和合同生成工具；bootstrap 会准备这些共同依赖。Testing 库须由部署者事先初始化；集成测试校验目标与开发库分离，替换全部带外驱动、禁用采集并只清理本次创建的数据，不自动迁库、不使用开发密钥。

工具通过不等于浏览器或设备通过。浏览器检查品牌登录/运行首页、头像语言、齿轮主题、服务器编辑/连接测试、库存、实时/历史、权限、防重、控制台与失败请求。真实设备只读检查和连续采样另记固件/能力、时间、字段缺口与恢复；没有提供的 RPM/PWM、累计和峰值不承诺可读。

## 异常信息与缓冲恢复

管理员在 `/oob/exceptions` 分页查看、筛选与复制脱敏诊断。正常校验拒绝、正常取消及设备能力缺失不生成系统异常；设备真实读取失败单独分类。硬件页面读取最后保存的库存，显式刷新才更新，三语言资源从 PC 的 `src/locales/{en-US,zh-CN,zh-TW}` 维护。

异常表由显式 EF 迁移 `20261024171205_SystemExceptionLogs` 建立，普通启动不迁库。本地缓冲默认放在 `Environment.SpecialFolder.LocalApplicationData` 对应的 `OutOfBandManagement/exception-spool/<EnvironmentName>`，独立于仓库、数据库连接及密钥。目录/文件限制为当前用户访问；数据库故障时先保留脱敏 JSONL，服务恢复后定时按同一ID重放、去重。不得把缓冲复制到共享仓库；本期不自动清理、截断或删除文件。

数据库异常尝试每批最多2秒，失败退避5秒；磁盘不可用等极端条件不能保证零丢失。恢复时保持缓冲和 checkpoint 配套，不手工修改偏移、生成新ID或重放设备操作。设计与验收边界见[ADR-0009](adr/ADR-0009-异常记录与业务结果分离.md)。

## 统一虚拟控制台与介质

入口为 `/oob/consoles`，也可从服务器列表/详情打开对应标签。打开标签和查询能力不创建 KVM；点击连接后使用独立后端会话，真实视频握手成功后再允许开启输入。标签切换保持连接，隐藏标签撤销输入；关闭标签、退出或过期关闭所属控制台。本地 ISO 使用浏览器 File 按需映射，关闭页面会影响介质读取；URL/平台镜像库挂载有独立生命周期，不能随标签关闭自动拔盘。

原生渲染脚本在运行时从已登记设备读取，按已审查摘要严格检查；新固件或资源变化须先审查，不能关闭摘要校验继续执行。只透传控制台固定协议及本次会话路径，不提供整个带外后台反向代理或电源菜单。输入、介质挂载/卸载与电源验收分别进行；自动测试不发送任何真实设备输入或介质动作。

当前资源审查覆盖 iLO4 2.82（兼容库存原文 `iLO 4 v2.82`）及 iDRAC8 2.85.85.85/2.86.86.86。2.86 的19份原生脚本已逐字摘要比对，与2.85清单一致，仍逐连接验证摘要。原生 Worker 必须可用，不能为 PseudoWorker 回退放宽 `unsafe-eval`。Driver.Tests 中的画布脚本探针需要已按本手册配置的 Node，使用真实交付外壳及内存 DOM/协议替身，不包含厂商制品或真实设备连接。

本地 ISO 与设备直接读取 URL 不要求配置平台镜像地址。只有选择“平台镜像库”时才需要 BMC 可达的 `VirtualMedia:PublicBaseUrl`；地址不能使用相对于 BMC 的本机回环地址。部署还需确认 DNS、证书、路由、防火墙及反向代理的 Range/HEAD、WebSocket Upgrade 和大文件限制。不同固件可用的协议与认证方式按实际能力验证，不照搬新代控制器。

可配置项为 `VirtualMedia:StorageDirectory`、`PublicBaseUrl`、`MaximumImageBytes`、`MaximumLibraryBytes`、`MaximumImages`。未指定存储目录时使用 `Environment.SpecialFolder.LocalApplicationData` 下的 `OutOfBandManagement/virtual-media/<EnvironmentName>`，不写仓库或 `.env`。默认单镜像16 GiB、库64 GiB/128文件；普通业务主键复用 IIdService。

镜像目录与 `media-journal.jsonl`、Data Protection 密钥须按同一恢复边界备份；原始来源 URL 在日志中加密，BMC 读取凭据仅保留摘要。日志追加前持久化意图，发送后未知不会重发；重启恢复只读核对设备状态。独占 `store.lock` 限定单实例，第二进程不得共用该目录写入。目录不可写、日志损坏或密钥不匹配须停止写操作，不能重建空记录冒充没有挂载。

失败上传不参与挂载，文件保留并计入容量；镜像没有自动删除、覆盖或保留期清理。关闭画面不等于卸载 URL/镜像库介质，人工确认不再使用后显式卸载。

## 用户人工电源验证

真实电源控制默认关闭：配置键为 `PowerControl.Enabled`。部署者应在忽略的私有配置文件（如 `.local/oob/application.json`）中明确设为 true，重启 API 后再进行人工验证；不得把开关默认值当作已启用。

1. 确认目标服务器、当前业务影响、维护窗口与拟执行动作；页面按设备正式能力显示动作。
2. 输入服务器名称并确认后发送一次；保存原 RequestId 和操作记录，区别 Submitted、Rejected、Unknown 与已核对结果。
3. 结果未知时查询原操作及设备实际状态，不重复点击、换 RequestId 或自动补发。不得把 HTTP 接受当作主机已完成动作。
   操作人确认现场结果后，可对原操作调用人工核对接口，填写服务器名称、Outcome（Confirmed 或 NotExecuted）及不超过 500 字的 Note。该接口只更新所属操作人的审计记录，不重新发送设备指令；不能把未核实结果填写为确认完成。
4. 验证结束按用户安排关闭全局开关并重启；记录实际执行者、动作、结果及未验证范围。

AI 没有真机电源测试的默认授权。即使用户曾授权设备读取、账号初始化、实现接口或全局开关已开启，每次代操作仍需具体服务器、动作和时机的明确许可。自动回归只使用模拟电源写入，不触达真机。

## 交付与接续

记录通过、失败、跳过、未运行及限制，保留首次失败，不代签人工验收或评审。当前本机可运行不证明生产部署、多副本采集、所有固件或所有浏览器已验收；贡献与验证要求见 [CONTRIBUTING](../CONTRIBUTING.md)。

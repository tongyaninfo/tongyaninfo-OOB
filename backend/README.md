# 带外管理后端

采用 .NET 10、EF Core/Npgsql 和 PostgreSQL 17/TimescaleDB。设计范围见[一期方案](../docs/spec/带外管理系统一期实施方案.md)，实际分层见[架构地图](../docs/architecture.md)与 [ADR-0004](../docs/adr/ADR-0004-业务后端采用分层架构.md)。

[桌面版](../desktop/README.md)通过共享 `OobApplication` 入口与 SQLite 存储子类复用本工程；服务端的默认 Npgsql、TimescaleDB 与原 EF 迁移链保留。桌面依赖和迁移位于 `desktop`，不将服务端数据库切换为 SQLite。

## 当前工程

| 目录 | 职责 |
| --- | --- |
| `src/OobManagement.Domain` | Models：用户、服务器、硬件快照、指标、采样、采集与电源意图 |
| `src/OobManagement.Application` | Models/Service：统一 ApiResult、请求响应和服务接口 |
| `src/OobManagement.Infrastructure` | Persistence、Common、Service/impl：EF、发号、凭据保护、驱动、采集与业务实现 |
| `src/OobManagement.Api` | 真实 HTTP 宿主、认证/防伪/权限、Controller、MCP/OAuth 及协议组装 |
| `tests/OobManagement.Driver.Tests` | 协议、单位、部分失败、资源链接及电源映射的受控测试 |
| `tests/OobManagement.Integration.Tests` | 真实 HTTP/EF/Testing 库，带外驱动全部替换为纯内存实现 |
| `tools/OobContracts` | 独立 MVC/端点/JSON Schema 离线导出；不调用业务宿主 |

统一接口 `IOutOfBandService` 由 `Ilo4Service`、`Idrac8Service` 实现，静态信息与实时读数分开。硬件、风扇与功率能力按实际设备资源发现；缺测和不支持保持显式状态，不转换为 0。

每台服务器保存 `IgnoreCertificateErrors`（默认 true），无指纹时可兼容带外自签名、名称不符等证书；明确 false 使用系统验证，显式指纹始终优先。策略只配置本设备的独立 Handler，不修改系统根证书或其它请求；TLS 握手与网络错误分开返回。

Dell 风扇在 Redfish 缺少 PWM 时，使用同源 HTTPS RACADM 固定只读 `getsensorinfo` 补充真实 PWM，精确匹配原风扇标识。所有品牌的百分比统一为 `fan.percent`，名称“转速百分比（%）”：优先有效原生 PWM，其次有效普通百分比，每个物理风扇只保存一条，不从 RPM 推算或把两个百分比平均。补充失败保留已有有效百分比及其他指标；只清理本次认证会话。

## 配置、启动与显式初始化

从仓库根执行：

```sh
dotnet run --project backend/src/OobManagement.Api/OobManagement.Api.csproj --no-launch-profile
```

API 默认 `http://127.0.0.1:5080`；Swagger/OpenAPI 服务、UI 与旧文档端点已退役，REST 合同由独立离线工具生成。`/health` 仅表示进程能响应，不证明数据库、设备、采集或业务验收已就绪。

宿主生产入口使用[HTTPS 80/443配置](../deploy/oob/HOST-SYSTEMD.md#当前-https-配置与证书维护)，独立于本机开发默认值。`HttpsRedirection:Enabled=true` 启用HTTP 308跳转至 `HttpsRedirection:HttpsPort`（默认443），同时让认证/CSRF Cookie使用Secure；TLS端点和证书路径由宿主配置提供，无证书时不自动生成或降级为明文。

本机私密配置在已忽略的 `.local/oob/`：`application.json` 提供初始化与运行选项，`database-connections.json` 提供 Development/Testing 连接，`keys/` 持久化设备凭据的 Data Protection 密钥。密码不进入接口响应、前端或公开日志；密钥备份与恢复必须和数据库一起考虑，生产密钥静态保护另按正式部署设计。

只有已经确认的初始化操作才执行：

```sh
dotnet run --project backend/src/OobManagement.Api/OobManagement.Api.csproj --no-launch-profile -- --initialize
ASPNETCORE_ENVIRONMENT=Testing dotnet run --project backend/src/OobManagement.Api/OobManagement.Api.csproj --no-launch-profile -- --initialize
```

第二条为 macOS/Linux Shell 示例。Testing 不导入真实服务器初始化配置。该入口显式应用 EF 迁移，仅在用户表为空时创建初始管理员，开发环境可读取本地种子配置；已有管理员密码不覆盖。普通 Web 启动不自动迁库。数据库容器、扩展与账号初始化见[数据库入口](../deploy/oob/README.md)；后续结构变更须按目标环境的流程单独确认。

## 用户与权限

用户管理支持姓名、账号、启停、逻辑删除及独立重设密码；非删除账号按数据库规范化唯一。UserManagement、VirtualConsole、ServerManagement、PowerOperations 为四项独立权限；新增用户默认只读。系统管理员也不能绕过取消的勾选。服务器管理覆盖配置、连接测试、UID及手动采集；电源操作覆盖动作、记录和核对。每次HTTP请求核验账号状态与安全版本，敏感变更撤销旧Cookie和实时通道，设备写入发送前再次核验。详见用户管理变更与设备权限扩展。

## 接口与采集

邮件复用现有系统管理员身份，提供单 SMTP 配置、连接检测、测试/手动发信和本系统发件列表；配置与发送结果仅有 `OOB_MAIL_CONFIGURATION` 和 `OOB_MAIL_MESSAGE` 两张表。密码使用宿主 Data Protection 的邮件独立 purpose 加密，更新留空保留；多邮箱来源、收信、审核和后台队列不在当前范围。发送意图先落库，同 RequestId 不重发，结果未知或重启遗留发送中按 Unknown 展示。

运行报表扩展现有两表保存日周月计划和可信HTML快照，默认关闭，后台每30秒核对最近到期周期。计划保存及预览不发信，统计仅读取已入库轮次、传感器和异常；停用/版本变化在SMTP提交前再次核验。API的`Resources/Mail/OperationReport.html`显式复制到build/publish目录，桌面共享模板并在数据库就绪后才启动调度。普通邮件仍为纯文本，报表提供HTML及纯文本备选；两套追加迁移均不由普通启动执行。

HTTP 业务接口统一返回 ApiResult 与实际 HTTP 状态；浏览器 Cookie 身份与 CSRF 配合，写请求携带 `X-CSRF-TOKEN`。MCP、OAuth、SignalR 和流式入口保留各自真实协议。实体 long 以字符串传输；时间存 UTC，展示转换一次。正式合同见[API](../docs/api/oob/README.md)。

采集器为当前单实例的有界后台循环，最多四台独立运行，完成一台即可释放容量；同一服务实例内每台服务器共享互斥门，采集与手动刷新不重叠。保存失败另有短时内存退避，避免旧到期故障设备持续抢占健康设备。设备读取90秒截止后仍保存失败轮次；采样间隔为 1–86400 秒的整数，不承诺设备按配置频率产生新传感器值。本期未实现多副本分布式采集租约。

历史数据使用 TimescaleDB 超表，复合主键 `(SeriesId, SampledAt)`，查询时间桶返回平均/最小/最大和有效数量，缺口保持空值。累计值、设备峰值与应用观察统计不得混淆；未启用自动删除历史、压缩或保留策略。

总览近期接口单独返回实际采样时刻与失败轮次，最多15分钟、窗口内有采样的256序列与250000点，不将没有样本的固定桶误作采集失败。历史退役序列不占近期上限，窗口内纯缺测序列仍保留，见温度看板与近期查询。静态硬件查询只返回上一次保存库存，显式刷新才访问设备。

异常通过 IExceptionRecorder 脱敏后先追加仓库外本地缓冲，再用独立 DbContext 写 OOB_SYSTEM_EXCEPTION；数据库恢复后按稳定 ID 去重补入。管理员可查看、复制安全诊断，业务拒绝使用结果、不抛异常。方案、敏感信息边界和恢复限制见[ADR-0009](../docs/adr/ADR-0009-异常记录与业务结果分离.md)。

采样持久化后经发布接口向只读 SignalR Hub `/api/oob/live` 推送 `TelemetryUpdated`，成功和失败状态均带持久化版本；推送失败不改变已保存结果。Hub 限定 WebSocket、有效登录 Cookie 和握手来源，退出、认证到期或账号安全版本变化关闭连接。重连不补发快照或历史，客户端只接收后续事件；无人在线也照常采样入库，当前没有跨实例分发或离线回放，见[推送合同](../docs/api/oob/README.md#实时推送合同)。

## MCP 查询服务

服务配置、白名单与调用日志由管理员管理；远程调用使用独立 OAuth Bearer，十个查询工具按原始同意快照与当前授权取交集，查询前、返回前均核验。所有工具仅读取已保存数据，不访问带外驱动或 SMTP。业务配置与审计由 OobDbContext 管理，OAuth 组件由独立 McpOAuthDbContext 和迁移历史管理；普通启动均不迁库。SDK 锁定 ModelContextProtocol.AspNetCore 2.3.0、OpenIddict 7.5.0，后者用于保持现有 EF Core 依赖兼容。配置、接入和限制见[MCP说明](../docs/api/mcp/README.md)与[ADR-0012](../docs/adr/ADR-0012-MCP服务授权与离线合同.md)；真实互通、完整自检和生产发布分别记录。

## 电源操作

`PowerControl.Enabled` 默认 false，用户在本机私密配置明确启用并重启 API 后，才按[用户人工电源验证](../docs/development.md#用户人工电源验证)自行测试。动作按能力校验，确认服务器名称后先持久化幂等意图，只发送一次；Submitted/Unknown 等待核对，不通过重试制造新意图。

自动测试只操作模拟驱动；任何 AI 真机电源动作均需逐次授权，后台采样、页面读取、服务启动或初始化不调用电源写入口。

电源页通过独立只读接口实时读取能力、主机状态及全局开关，不依赖库存缓存。UID/电源命令可等待当前采样完成，最多90秒；取消排队不补发，取得锁后和设备发送前再次验证权限。短按为 PushPowerButton，长按强制关机效果为 ForceOff，优雅关机保持独立，见UID与电源控制变更。

## 检查与验收

```sh
node scripts/verify.mjs --scope backend
node scripts/oob-contracts.mjs check
```

后端范围运行真实解决方案构建、驱动测试、真实 Testing 数据库集成测试及合同检查；它不是生产或真机电源验收。测试前先完成锁定依赖恢复和 Testing 库初始化，缺配置/未迁移须失败，不能回退开发库。

控制台新增独立会话与厂商原生通道，虚拟介质拥有独立生命周期，见[运行说明](../docs/development.md#统一虚拟控制台与介质)。驱动测试中的原生画布回归由 Node 执行实际交付 HTML，覆盖104键、来源/挑战、输入撤销、画布留黑与缩放后的鼠标释放，不访问真机或携带真实凭据。

| 层次 | 重点 |
| --- | --- |
| 驱动 | 资源发现、同源限制、固件差异、单位、PWM/RPM、部分资源失败及只发一次 |
| HTTP/数据库 | 实际路由、认证、CSRF、统一错误、长 ID、加密持久化、状态/幂等、超表历史与设置隔离 |
| 真实设备只读 | 两类控制器当前固件的库存、传感器、持续采样、断线恢复与缺测语义 |
| 浏览器/人工 | 登录退出、权限、设置、历史、原模板外观；真机电源由用户单独验收 |

集成测试使用 Testing 连接、唯一夹具前缀和临时身份，关闭采集并替换所有带外驱动；不自动迁库、不替换开发密钥、不删除文件或已有结构。最后管理员并发测试在明确的Testing库创建独占用户表schema并保留取证，不改公共用户数据。只清理本次夹具数据，失败和残留如实报告。执行日志及报告留仓库外，覆盖率或历史通过数不能代替最新检查。

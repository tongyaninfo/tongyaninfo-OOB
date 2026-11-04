# 带外管理 API

本模块提供认证、用户管理、账号设置、服务器管理、硬件/采样/历史、邮件、MCP 管理以及显式主机电源接口。代码源为[后端 Controller](../../../backend/src/OobManagement.Api/Controllers)与 Application 模型；正式合同为[routes.json](routes.json)与[schemas.json](schemas.json)，PC 消费类型由固定生成器生成。

## 入口与身份

本机 API 为 `http://127.0.0.1:5080`；前端通过同源 `/api/` 代理访问。Swagger/OpenAPI 服务、文档端点与 UI 已退役，旧路径返回真实 404。业务响应使用 ApiResult 并保留 HTTP 状态；`/health`、SignalR、MCP 与 OAuth 使用各自真实协议，不套用业务信封，也不证明其他依赖就绪。

| 路由组 | 用途 |
| --- | --- |
| `/api/auth/csrf`、`/api/auth/login`、`current-user`、`logout` | 防伪令牌、真实登录、当前身份、退出 |
| `/api/auth/change-password` | 当前账号校验原密码后改密，旧会话失效 |
| `/api/users`、`{id}`、`{id}/delete`、`{id}/reset-password` | 用户列表、新增编辑、逻辑删除及独立重设密码 |
| `/api/preferences` | 当前账号外观/表格白名单设置 |
| `/api/oob/dashboard`、`servers` | 总览、列表、新增与编辑 |
| `/api/oob/servers/test-connection` | 显式连接测试，不自动创建服务器 |
| `/api/oob/servers/configuration/export`、`configuration/import` | 服务器配置加密导出和原子导入，重复配置相同跳过、不同覆盖 |
| `/api/oob/servers/{id}` | 详情、最近读数、手动采样、硬件刷新、历史及电源记录 |
| `/api/oob/servers/{id}/recent` | 首次最近15分钟实际采样点与失败轮次，不补空桶 |
| `/api/oob/exceptions`、`{id}`、`client` | 管理员分页、脱敏详情及受限客户端上报 |
| `/api/oob/mail/configuration`、`probe`、`test`、`send` | 管理员单SMTP配置、显式连接检测、测试/手动发送 |
| `/api/oob/mail/messages`、`messages/{id}`、`requests/{requestId}` | 管理员查看本系统发件列表、纯文本详情及原请求结果 |
| `/api/oob/live` | SignalR WebSocket 授权推送；不是普通 JSON 业务接口 |
| `/api/oob/servers/{id}/console-capabilities`、`console-sessions` | 控制台能力查询及显式创建会话 |
| `/api/oob/console-sessions/{id}` | 所属会话状态、流量、输入开关及显式关闭 |
| `/api/oob/media-images` | 可选平台镜像库分页及原始镜像流上传 |
| `/api/oob/servers/{id}/virtual-media` | 介质槽、挂载、卸载、原请求核对及平台读取流量 |
| `/api/mcp/configuration`、`configuration/enable`、`configuration/disable` | 管理员读取/保存设置及显式启停，保存不自动启用 |
| `/api/mcp/clients`、`clients/{id}` | 管理员分页、新增/编辑调用方与工具/设备/网络授权 |
| `/api/mcp/tools`、`options` | 当前真实工具目录及白名单选择项 |
| `/api/mcp/logs`、`logs/{id}`、`configuration-audits` | 管理员调用日志及配置变更审计 |
| `/mcp`、`/connect/*`、`/.well-known/*` | MCP、OAuth 与公开协议发现，按真实协议返回 |

方法、参数、错误码、可空与响应模型以正式路由/JSON Schema、接口说明和实现为准，表格不替代合同。有效登录账号可读取看板、服务器和历史；配置、连接测试、UID和手动采集要求 ServerManagement，电源动作、操作记录及核对要求 PowerOperations；异常日志查看仍限 admin。用户管理要求 UserManagement，控制台及虚拟介质要求 VirtualConsole，admin 不绕过四项勾选。会话采用 Cookie，写请求需 `X-CSRF-TOKEN`，登录/退出后按新身份重新获取令牌。设备密码不出现在明文响应或前端持久存储；显式导出仅返回使用文件密码加密的配置包。

## 单邮箱发件合同

邮件接口均要求有效系统管理员身份，业务服务重新核验AuthVersion。`GET/PUT /api/oob/mail/configuration`读取/保存唯一配置，首次提供SMTP主机、发件邮箱和密码，端口默认465、securityMode默认SslOnConnect，可选StartTls；username留空使用发件邮箱。编辑password留空保留密文，响应只含passwordConfigured，不含密码或密文。保存携带字符串version，过期返回MAIL_CONFIGURATION_CONFLICT/409；保存和读取不连接SMTP。

`POST probe`只连接和认证已保存配置，返回connected、errorCode和固定安全message。`POST test/send`接收稳定requestId、单个toAddress、subject（最多200字符）和textBody（最多20000字符）；接口分别标记Test/Manual，先保存意图再发送一次。同标识相同内容只返回原记录，不同内容返回MAIL_REQUEST_CONFLICT/409。所有POST/PUT均校验CSRF。TLS严格校验，网络白名单由部署Mail配置节提供，不由表单扩大。

状态Sending表示当前进程正在提交，Accepted仅表示SMTP服务器受理，Failed表示明确失败，Unknown表示无法确认；重启遗留Sending按Unknown展示且不写状态或重发。`GET requests/{requestId}`只查询原意图，明确不存在返回MAIL_MESSAGE_NOT_FOUND/404。页面遇到结果未知保留请求标识，只有明确查无记录后才允许用户按原标识再次提交。

`GET messages`支持current/pageSize（最多100）、keyword（主题/收件人）、status、kind和sortField/sortOrder；排序字段为toAddress/subject/kind/status/createdAt/completedAt，方向ascend/descend，默认创建时间倒序并以ID稳定排序。列表不查询正文；`GET messages/{id}`详情返回纯文本正文。配置、正文及发件响应禁缓存，ID/version为字符串、时间UTC。完整范围见迁移记录。

## 服务器配置迁移合同

两个配置迁移入口均为 `POST`，要求 ServerManagement 权限、有效会话和CSRF，沿用 `ApiResult`。`configuration/export` 接收 `{password}`，返回全量配置的 `ServerConfigurationFile` 密文信封，响应禁缓存；`configuration/import` 接收 `{password,file}`，返回 `{added,updated,skipped}`。文件密码8至128字符且不能全为空白，不保存或回传；服务器设备密码仅存在于认证加密的文件内部，前端不能取得明文。

文件按版本校验并可跨宿主导入；按规范 HTTPS 地址与控制器类型匹配，相同跳过、不同覆盖、缺少新增，原主键与监控历史保留。整包预检失败不写入；密码错误、篡改及未知版本返回 `INVALID_CONFIGURATION_FILE/400`，文件内重复目标返回 `DUPLICATE_CONFIGURATION_SERVER/400`，限额返回 `CONFIGURATION_LIMIT/422`，并发唯一约束冲突返回 `SERVER_CONFLICT/409`，源凭据不可解密返回 `CREDENTIALS_UNAVAILABLE/503`。最多1000台、明文2 MiB、导入请求4 MiB。导入不访问设备，启用的配置仍按原后台调度采样；网络结果未知不自动重试。完整格式与行为见变更依据。

## 用户与改密合同

账号按数据库 upper/btrim 规则规范化，启用和停用账号均占用名称，仅逻辑删除后可复用；新建使用新ID，不继承原账号设置或审计。列表支持服务端状态/关键词筛选及稳定排序；status=all 表示未删除，deleted 单独查询。编辑、删除及重设密码携带当前 version，过期返回 USER_VERSION_CONFLICT/409；重名返回 ACCOUNT_EXISTS/409。

设备权限迁移为既有系统管理员回填两项新权限，普通账号默认不授予；新增账号四项权限默认均为 false。系统管理员身份不会隐式授权设备操作；普通用户管理员不能修改系统管理员。事务保护最后一个启用的用户管理员、系统管理员及同时具备两者的账号。当前用户合同返回明确布尔权限，密码哈希与内部安全版本不外发。自助改密校验原密码，管理端重设密码不读取旧密码；新密码8至128字符，不能全为空白。密码、账号、状态或权限变化使该账号旧Cookie、遥测及控制台连接失效，部署前的旧Cookie需重新登录。

独立介质挂载不随改密、控制台撤权自动卸载；已挂载平台镜像继续按原设备/账号有效性校验供给，停用/删除账号不再提供新读取。详见用户管理变更与设备权限扩展。

## 实时推送合同

`GET /api/oob/live` 通过 WebSocket Upgrade 建立 SignalR JSON 协议连接，客户端指定 `WebSockets` 和 `skipNegotiation: true`，服务端也只允许 WebSocket。沿用同源有效登录 Cookie，校验握手 Origin，认证到期、退出或账号安全版本变化时终止相应连接。Hub 没有任何设备操作方法。

事件 `TelemetryUpdated` 的唯一参数为 `ServerTelemetryDto`（`server`、`telemetry`、`publishedAt`），同一类型由 `DashboardDto.telemetrySnapshots` 引用并纳入离线 JSON Schema 生成链。`publishedAt` 表达已持久化版本，客户端逐服务器比较，不能将延迟快照或旧事件覆盖新值。采样成功和失败均在保存后发布，失败保留上次快照及 Offline 状态，发布失败不回滚数据库。

首次进入页面读取持久化快照和历史，此后只使用推送；重连不读取快照、不补取历史、不回放离线消息。断线期间数据库继续保存采样，用户可主动进入历史页查询。SignalR 帧和握手按其协议返回，不包装为 ApiResult；普通 HTTP 业务接口仍沿用统一信封。依据：[Microsoft SignalR 配置](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration?view=aspnetcore-10.0)、[认证](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0)。

## 指标、时间与状态

控制台与介质细节见统一控制台变更。`POST servers/{id}/console-sessions` 只接受稳定 RequestId，不允许浏览器指定转发目标。创建响应中的 Connecting 表示后端身份/资源已准备，真实厂商视频握手成功后才转 Connected。输入另外显式启用；关闭、退出、过期、失焦与隐藏标签释放输入，不回放旧事件。

控制台资源 `/api/oob/console-sessions/{id}/frame/...` 与流 `/stream/...` 不套 JSON 信封；分别返回固定内容类型和 WebSocket 协议，严格绑定用户、会话、路径和 Origin。资源启用 CSP、禁缓存和固定摘要审查；设备密码及真实原生会话密钥不进入浏览器。厂商静态资源不提交仓库。

会话累计 bytesReceived/bytesSent 为长整数字符串，表示平台可观测的资源及控制台传输载荷；mediaBytesReceived/mediaBytesSent 是其中原生介质通道子集，不能再加到合计。平台镜像 HTTP 读取由 VirtualMediaTrafficDto 另外报告，不含浏览器本地 ISO 通道，也不包含 BMC 直连外部 URL 的流量。

`POST /api/oob/media-images?fileName=...` 接收 `application/octet-stream` 原始文件流，需要 Content-Length 与防伪令牌；最大单文件16 GiB、默认镜像库64 GiB/128文件，实际可用量受运行配置约束。上传只保存镜像，不隐式挂载。ISO 校验 ISO9660/UDF 标识，IMG 当前只接受1.44 MB。失败或不完整文件不入镜像列表且不自动删除。

`POST servers/{id}/virtual-media/mount` 在 ImageId 与 ImageUrl 中只能选择一个，须给出设备声明的 SlotId、服务器名称确认及 RequestId；Eject 只允许对设备当前状态与平台已记录来源匹配的介质执行。Submitted/Unknown 均不能当作挂载完成；查询原 RequestId 后与设备当前插入/写保护状态核对，不重发。挂载不改变启动项或执行电源动作。

`POST servers/{id}/virtual-media/operations/{requestId}/review-not-executed` 仅在 `canReviewNotExecuted=true` 时由原操作人填写服务器名称和核对依据，结束原未知意图为 NotExecuted；服务端再次读取当前设备，不发送新动作。目标地址/控制器已变化、当前状态矛盾或原意图已有终态时拒绝人工改写，不能把一次空槽查询自动当作历史未执行。

BMC 读取平台镜像使用每次挂载的高熵只读凭据和固定文件入口，支持 HEAD 与单 Range 的206/416响应；该流不使用浏览器登录 Cookie，卸载确认后撤销授权。内容入口不是任意 URL 或磁盘路径代理，不能把含读取凭据的完整路径写入日志。

实体 long 为十进制字符串，不转 JavaScript number。数据库与接口时间携带明确 UTC 语义，展示转 Asia/Shanghai 一次。历史查询区间、时间桶及指标筛选按实际参数处理；空读数、缺测、不支持与 0 分开。

历史桶的平均/最小/最大和数量仅统计 `Availability=Available` 的非空读数；纯不可用或无样本桶保持空值/0，不把无效非空值计入。聚合首页和单机总览历史弹窗复用此本地数据库查询接口，提供一天/一周/自然月预设、自选日期时间与聚合粒度说明。区间采用 `from,to)`，单次跨度最多366天，服务端按约1000桶限制调整粒度；第一桶时间可早于起点，但桶内仅包含查询区间的数据，见[历史范围。

`SaveServerRequest.ignoreCertificateErrors` 默认 true，ServerDto 回显保存值；显式 false 必须按原值存储。该策略仅作用当前设备，无指纹且为 true 时忽略证书错误，为 false 时使用系统验证；`certificateFingerprint` 非空始终优先匹配，即使开关为 true 也不能绕过错误指纹。所有读取及受控写入口使用相同连接策略，HTTPS 和同源限制保持适用。协议细节见 [.NET 证书验证回调](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.servercertificatecustomvalidationcallback?view=net-10.0)。

采样与硬件刷新是显式读取设备并记录结果的动作；页面读取最近值和历史不隐式发电源请求。按能力提供功率/BTU/h、温度和风扇数据，来源实际、推导和未知应区分。设备没有的 RPM、百分比或累计值不能凭其他读数伪造。

风扇业务指标为 `fan.rpm`（RPM）与 `fan.percent`（%，统一名称“转速百分比（%）”）。HP、Dell 每个稳定风扇资源键均只返回一个百分比；驱动优先选择有效原生 PWM，其次有效普通百分比，0–100 的真实0保留，缺测保持空值及质量状态。历史数据、近期、最新快照、能力清单和推送采用同一指标，旧 `fan.pwm.percent` 通过受审的数据迁移合入，前端不再合算两路百分比。

`GET /api/oob/servers/{id}/uid` 只读取 UID 状态与本轮发现的控制能力；`PUT` 同路径接收 `SetUidRequest`（必填 `state=Off/Lit/Blinking` 及服务器名称 `confirmation`），均要求 ServerManagement 权限，写入额外校验 CSRF。`UidStatusDto.state` 为 `Off/Lit/Blinking/Unknown`，`supportedStates` 只列本设备正式支持的状态：本期 HP 为 `Off/Lit`，Dell 为 `Off/Blinking`，不支持的值在写入前拒绝，`observedAt` 为 UTC。写后必须回读设备；`UID_RESULT_UNKNOWN` 返回真实 HTTP 错误及 `data.state=Unknown`，不能以目标状态替代设备结果。前端手动 GET 核实后才允许下一次显式写操作，不自动换状态或重发，见UID 与真机验证。

UID 页面已取消输入确认，`confirmation` 自动使用当前服务器名称并继续由后端核验目标一致性；写入口等待当前采样结束，最多90秒，取消或超时不补发。

`GET /api/oob/servers/{id}/power-capabilities` 要求 PowerOperations 权限，返回 `PowerCapabilitiesDto`（`enabled`、实时 `capabilities`、`powerState`），只读且不持久化库存或创建电源意图。页面动作不再依赖旧库存能力；加载和失败时不沿用旧动作。`PushPowerButton` 表示短按，`ForceOff` 表示长按强制关机效果，`GracefulShutdown` 独立表示优雅关机，具体动作仅按设备声明开放。

电源能力查询及写前预检只发现唯一主机和必要 ActionInfo，不读取管理器、机箱或遥测。执行入口预检读取失败返回 HTTP502 + `POWER_PRECHECK_FAILED`，明确本次未创建意图、未发送设备命令；客户端仅将这一明确业务码识别为未发送，不将其他502或网络中断泛化为安全失败。成功读取但确实缺少所选动作才返回 `POWER_UNSUPPORTED`。用户可再次明确发起操作，系统不自动重试电源命令。

电源写接口要求 PowerOperations 权限，只接受正式 PowerAction、RequestId 和服务器名称确认，不转发任意 URL/JSON。默认 `PowerControl.Enabled=false`；排队最多90秒，取消未发送，取得锁后及发送前再次核验权限。Submitted/Unknown 不等于完成，重复请求复用原意图，待核对时不创建新动作。真机操作由用户按[人工验证说明](../../development.md#用户人工电源验证)执行，AI 每次代操作须另有明确授权。

`POST /api/oob/servers/{id}/power-operations/{operationId}/review` 由原操作人填写名称确认、Outcome（Confirmed/NotExecuted）和核对依据，只更新原审计，不调用设备写方法。人工结论与设备只读核验分别留存，不将重启前后同为 On 误判为重启已完成。

## 近期数据与异常信息

`GET /api/oob/servers/{id}/recent?from&to` 返回 RecentTelemetryDto，series 沿用 HistorySeriesDto 的点结构，但时间为实际 SampledAt；有效点 count=1，不可用点为 null/count=0，不填空桶。gaps 为失败 PollRun 的 from/to；采集保存失败连 PollRun 也无法提交时，以恢复后的对应异常发生时刻提供单点缺口（from=to），不虚构故障持续时长。区间须有效且不超过15分钟，最多256序列、250000点；普通聚合历史接口继续保留。该接口仅供首次或用户主动读取，重连不调用。

`GET /api/oob/exceptions` 支持 current/pageSize、from/to、kind/level/source/serverId/keyword，按时间和ID稳定倒序；列表不加载堆栈。`GET /api/oob/exceptions/{id}` 获取脱敏详情。两者仅管理员可读，长ID为字符串、时间为UTC，无删除或清空接口。

`POST /api/oob/exceptions/client` 要求有效登录会话、防伪令牌，并限制请求大小和每分钟上报次数；仅接收事件种类、异常类型、代码帧及无查询串路径。服务端固定类别、摘要、用户及时间，未知客户端类型归为 ClientError，不能上报任意正文或伪造服务端异常。业务拒绝、正常取消与上报失败不形成递归异常。见[异常记录决定](../../adr/ADR-0009-异常记录与业务结果分离.md)。

## 同源生成与校验

```sh
node scripts/oob-contracts.mjs generate
node scripts/oob-contracts.mjs check
```

独立[离线工具](../../../backend/tools/OobContracts/README.md)从 MVC 的 ApiExplorer 与 EndpointDataSource 导出实际方法和路由，包括 IgnoreApi 的流式入口；非 MVC 协议使用正式路由常量登记。模型从 JsonTypeInfo/JsonSchemaExporter 及现有序列化配置生成，补齐 DataAnnotations、长 ID、请求/响应必填差异与集合可空语义。动态 ServiceResult 错误、文件协议、业务限额和副作用仍以本说明和服务测试核验，不声称反射能够自动推导全部运行分支。

固定本地 `json-schema-to-typescript 16.0.0` 生成 `frontend/PC/src/services/oob/generated.ts`，继续提供 `components.schemas` 类型兼容出口；`models.ts` 只提供类型别名。check 在仓库外生成 `routes.json`、`schemas.json` 和 PC 类型并逐字比较，同时验证长 ID、枚举、缺测、递归、泛型分页及必填字段，不删除临时目录、不访问私密配置/设备/DB或执行 Git。Schema 使用经适配的 draft-07 子集，未知关键字必须阻断生成。

接口增改、废弃和删除必须同时更新源、合同与受影响消费者，不能手改生成类型掩盖漂移。合同通过、PC 类型、真实 HTTP/数据库、浏览器、两类设备读取和用户电源验收分别记录。

[接口总入口](../README.md) · [后端](../../../backend/README.md) · [当前工具](../../../scripts/README.md)

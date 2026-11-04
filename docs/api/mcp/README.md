# MCP 配置与接入

本文说明当前实现的配置和调用合同。架构选择见 [ADR-0012](../../adr/ADR-0012-MCP服务授权与离线合同.md)。协议互通、实际客户端兼容和发布状态须按各部署环境自行验证，本文不代表生产已部署或所有客户端已经通过。

服务端通过 `ModelContextProtocol.AspNetCore 2.3.0` 提供同宿主 `/mcp`，OAuth 使用 `OpenIddict 7.5.0`，版本以根[集中依赖](../../../Directory.Packages.props)为准。不需要大模型 API Key，不接受静态 API Key 或浏览器 Cookie 代替远程 MCP 身份。

## 管理入口与默认行为

系统管理员可进入“系统管理 → MCP配置”(`/system/mcp`) 和“MCP调用日志”(`/system/mcp/logs`)。管理页面和 `/api/mcp/*` 使用现有 Cookie、CSRF 与 ApiResult；远程 `/mcp` 使用独立 OAuth Bearer 和 MCP 原生响应。管理员身份不豁免绑定用户当前的四项业务权限。

服务默认关闭，新调用方默认停用，工具和网络默认无授权。读取页面、查看目录、保存字段及复制说明均不启动服务、不授权、不发证、不主动采集设备。启用/停用使用独立动作；保存和所有管理变更携带字符串 `expectedVersion`，过期返回 `MCP_VERSION_CONFLICT/409`，应重新读取后审查，不能盲目覆盖。

## 宿主私有配置

以下属性位于现有获准宿主配置的 `Mcp` 节；生产常用被忽略的私有 `application.json`，具体位置沿用 `Oob:PrivateDirectory`。示例仅为占位符，不可直接启用：

```json
{
  "Mcp": {
    "PublicBaseUrl": "https://oob.example.invalid",
    "SigningCertificatePath": "<absolute-private-directory>/mcp-signing.pfx",
    "EncryptionCertificatePath": "<absolute-private-directory>/mcp-encryption.pfx",
    "CertificatePassword": "<provided-by-approved-private-config>",
    "KnownProxies": []
  }
}
```

| 属性 | 语义 |
| --- | --- |
| `Supported` | 服务端默认 true；桌面宿主强制 false，不能借管理界面开启外部访问 |
| `PublicBaseUrl` | 规范 HTTPS 根地址，只允许根路径，无用户信息、查询或片段；MCP resource 固定为该根下 `/mcp` |
| `SigningCertificatePath` | 已有PKCS#12签名证书的绝对私有路径，必须含RSA私钥且处于有效期内 |
| `EncryptionCertificatePath` | 已有PKCS#12加密证书的绝对私有路径，必须含RSA私钥且处于有效期内 |
| `CertificatePassword` | PKCS#12 密码，仅通过获准私有机制提供，不写源码、公共示例或日志 |
| `KnownProxies` | 可信反向代理 IP；空数组禁用转发头信任，不等于信任所有代理 |

普通启动读取既有证书，不自动生成、轮换、复制或修补生产秘密。TLS 站点证书与 OAuth 签名/加密证书职责不同，不能仅凭站点 HTTPS 正常就认为 OAuth 材料齐备。证书读取、私钥或有效期不满足要求时报告未就绪，不能回退为无认证服务。

反向代理需保留规范公开 Host/协议、按目标客户端支持 MCP 请求范围流式响应，并采用正确的请求大小/超时配置。仅从明确的 `KnownProxies` 接受 `X-Forwarded-For/Proto/Host`，当前转发限制为一跳。无法核对实际来源时应修正部署配置，不开放所有网络作为临时解决办法。

[三服务 Compose 示例](../../../deploy/oob/harbor/README.md)的 PC 镜像独立代理 `/mcp`、`/connect/*` 和 `/.well-known/*`，关闭协议缓冲和这些路径的 Nginx access log；Swagger/OpenAPI 路径返回 404。代理按本跳实际来源/协议覆写转发头，避免采信外部伪造值。默认 Compose 仍为 HTTP，外层 TLS→PC→API 的两跳不会自动获得正确的 HTTPS 和客户端地址；启用前必须按真实拓扑确认 TLS 终止位置、受信代理和来源处理。

现有 Compose 私有挂载不自动包含 OAuth PFX。由部署者在覆盖配置中增加证书目录的只读挂载，使上表两证书属性匹配容器内绝对路径；不将 PFX 或密码打包进镜像，也不因部署创建或轮换秘密。现有 `application-data` 卷已覆盖 MCP 审计缓冲。镜像仓库通过 `OOB_REGISTRY` 指定（示例占位为 `registry.example.com`），仓库地址与 `Mcp.PublicBaseUrl` 分别配置。

## 数据库与服务配置

业务设置、调用方、工具/设备/IP 规则、调用日志及配置审计共七张 MCP 业务表，使用现有 OobDbContext 迁移。OAuth 客户端、授权与令牌使用独立 McpOAuthDbContext 与迁移历史表 `__McpOAuthMigrationsHistory`；它们由组件链管理，不与七张业务表重复所有权。普通启动不创建表或自动迁移，结构缺失时保持未就绪。目标环境迁移与恢复必须有对应授权。

已有库升级须核对实际应用身份与有效 schema 的两张历史表：业务 `__EFMigrationsHistory` 应包含 `20261102130630_AddMcpManagement`，OAuth 历史应包含 `20261102130635_AddMcpOAuthStorage`，此前待执行版本也须按正式顺序处理。采用已审查的纯 EF 迁移入口，先业务、后 OAuth；不要直接套用新空库 `db-init`，或将带 Bootstrap 用户/设备处理的 `api-init`/`--initialize` 当作仅迁移命令。各环境的实际执行结果须单独记录，不推定其他环境已升级。

管理员保存以下业务参数后，再单独启用服务：

| 字段 | 默认值 | 当前校验范围 |
| --- | --- | --- |
| `requestsPerMinute` | 60 | 每调用方每分钟 1–6000，无等待队列 |
| `clientConcurrency` | 4 | 1–32 |
| `globalConcurrency` | 16 | 不小于单调用方并发，最多 128 |
| `queryTimeoutSeconds` | 30 | 1–120 秒 |
| `maxRequestBytes` | 262144 | 1024–1048576 字节 |
| `maxResponseBytes` | 2097152 | 1024–8388608 字节；实际响应边界由协议实现和验证核对 |
| `enabledTools` | 空数组 | 仅接受真实十工具目录，不通过通配符自动放行未来工具 |

`enabled`、`supported`、`ready`、`readinessErrors`、`endpoint`、`protocolVersions`、`version` 为读取状态；`enabled` 由显式启停动作维护。启用前检查宿主、业务/OAuth 迁移及有效调用方白名单，错误不能靠保存草稿绕过。停用不停止整个系统或后台采集；新调用被拒绝，当前进程活跃查询收到取消，查询返回前仍重新校验授权。

## 调用方与白名单

管理员预注册名称、`clientType=public/confidential`、绑定 `userId`、到期时间、工具、服务器、网络、浏览器 Origin 和 redirect URI。OAuth client ID 由系统生成，是公开标识，不是秘密；客户端上报名称不能决定身份。

- `SelectedServers` 只允许明确选择的设备；空集合拒绝全部，新增设备不会自动加入。
- `AllServers` 为显式全部授权，包含今后新增设备；不得通过“当前设备刚好全部选中”推导它。
- IP/CIDR 必须非空，支持 IPv4、IPv6 和 IPv4-mapped IPv6 的规范化；不默认信任回环、私网或代理。
- Origin 存在时与登记值精确匹配；不带 Origin 的普通客户端仍须通过 token 和 IP 校验。HTTPS Origin 或回环 HTTP Origin 不包含路径、查询、片段。
- redirect URI 精确登记，允许 HTTPS 或本机回环 HTTP；不接受通配符。普通公网 HTTP 回调不允许。
- public 客户端采用 PKCE，不分发共享密码；confidential 客户端仅在管理员显式生成/轮换时一次显示新秘密，后续不回显。

保存调用方不自动发证。编辑已启用调用方时先暂停接入，再同步组件和业务策略，失败保持安全停用；不要把失败响应理解为原策略仍然可用。停用阻断当前访问；“撤销授权”还撤销既有组件授权及令牌。轮换秘密先停用并撤销旧授权，完成后需重新核对和显式启用。

新建保密调用方和公共转保密的调用方必须先显式“轮换密钥”完成首次发证，再启用；未发证时返回`MCP_SECRET_REQUIRED/409`。认证类型改变后保持停用，不沿用旧类型的接入资格。

## OAuth 与 MCP 接入顺序

1. 管理员完成两套增量迁移、宿主 HTTPS 和既有证书配置，核对未就绪原因；保存全局工具及限额。
2. 预注册客户端、绑定用户、精确回调和白名单，显式启用调用方，再显式启用服务。
3. 客户端读取 `/.well-known/oauth-protected-resource/mcp`；授权服务器发现地址为 `/.well-known/oauth-authorization-server` 或 `/.well-known/openid-configuration`。发现仅返回公开信息，不创建授权。
4. 客户端生成 `state`、PKCE verifier 和 S256 challenge，打开 `/connect/authorize`，携带 `response_type=code`、`client_id`、精确 `redirect_uri`、`scope=mcp.read`、`resource=https://<公开域名>/mcp` 及 PKCE 参数。
5. 用户通过本系统浏览器账号登录，查看调用方、工具和服务器范围，明确同意或拒绝。授权表单校验 CSRF 与展示时的用户/策略版本；需要持续离线访问时显式申请并确认 `offline_access`。
6. 客户端向 `/connect/token` 以表单兑换授权码，提供原回调、client ID、code verifier 和正确 resource；保密客户端另外按 OAuth 标准认证。授权码和 refresh token 不写 URL、日志或接入说明。
7. 使用 `Authorization: Bearer <access-token>` 调用 `/mcp`。产品目标协议为 `2026-07-28`，兼容 `2025-11-25`；由目标客户端/SDK按对应版本执行发现或初始化及元数据，不能混用两版生命周期。
8. 用户同意 `offline_access` 后可按标准 refresh_token grant 刷新，刷新保留原同意范围，不因后台新增授权自动扩大旧令牌。撤销使用 `/connect/revoke` 或管理员撤销动作。

access token当前15分钟，refresh token初始期限7天，由组件轮换且重用宽限为零；同一旧刷新令牌不可重放，客户端收到刷新结果后须使用新令牌，结果未知时不盲目重放旧值。每请求额外验证当前用户、安全版本、客户端及授权状态，撤权不依赖自然过期。令牌的issuer、audience/resource必须与规范公开地址匹配，其他服务token不接受。组件请求日志限制为Warning以上，详细认证参数由本系统脱敏审计替代。

本期采用无状态 Streamable HTTP，不额外建设旧式 `/sse` 服务；兼容版本的实际握手由固定 SDK 和目标客户端测试决定。没有承诺未登记客户端的动态注册，也没有承诺任意客户端都支持这两个目标版本。应在交付记录登记实际客户端及版本、OAuth/PKCE、目录、调用、错误和取消结果。

## 十个查询工具

工具目录按当前调用方授权过滤，调用时再次授权。输入拒绝未知字段，结果使用带 `schemaVersion` 与 `data` 的结构化输出，附必要文字说明。全部工具只读已保存数据，不主动访问设备、SMTP 或控制台。

| 工具 | 参数摘要 | 范围和限制 |
| --- | --- | --- |
| `oob_list_servers` | keyword、brand、current、pageSize | 获准范围内安全设备信息、分页和 total，按名称/ID稳定排序 |
| `oob_get_server` | serverId | 已保存设备状态，不输出带外管理账号/地址和证书策略 |
| `oob_get_inventory` | serverId、category | 保存的库存、时间与来源；未采集明确返回 NotCollected |
| `oob_get_latest_telemetry` | serverId、metricCode | 保存读数、单位、质量和陈旧状态，不主动采样 |
| `oob_query_history` | serverId、from、to、metricCode、bucketSeconds、maxPoints | 最多 7 天，每序列最多 2000 点、最多 256 序列；实际桶宽随窗口和点数约束调整并回传 |
| `oob_get_overview` | from、to、metricCode | 最多 7 天、64 台设备与 256 序列的范围内状态/趋势；不泄露范围外数量 |
| `oob_list_exceptions` | from、to、source、kind、分页 | 管理员且显式 AllServers，最多 31 天，默认最近一天 |
| `oob_get_exception` | exceptionId | 同上，只返回安全诊断字段，不输出原始堆栈/请求正文 |
| `oob_list_power_operations` | serverId、from、to、status、分页 | 当前必须具备 PowerOperations；最多 31 天，只查历史，不执行动作或核对 |
| `oob_preview_operation_report` | from、to、period | 管理员，最多 31 天；结构化采集/传感器统计，不返回任意 HTML，不发邮件 |

时间要求带时区 ISO 8601，范围为 `[from,to)`；历史/报表必填 from/to。总览不指定窗口时默认最近15分钟，异常列表和电源记录默认最近24小时；from/to 必须同时提供或同时省略。报表 period 默认 month，仅为 day/week/month 类别标签，不自动将显式窗口改成自然日/周/月，也不把采集成功率称作在线率。分页默认 20、最多 100，current 最大 1000000。超出结果上限明确失败，不能把截断数据当作完整结果。

业务 ID 和 long 字段均为十进制字符串，JSON-RPC id 保留合法协议类型，两者不能互相替代。读数保留 UTC 时间、来源、单位及缺测/不支持，历史保留断点；RPM 和百分比不混算，不用零值或上次有效值补造当前读数。范围外设备统一不可访问，不透露是否存在。

## 调用日志与审计

管理员在 `/system/mcp/logs` 按时间、调用方、用户、方法、工具、设备、来源 IP、结果、错误码、TraceId 和调用 ID 查询。默认最近 24 小时，单次最多 31 天，服务端分页/排序，默认发生时间倒序再 ID 倒序；可按耗时排序。列表不加载参数和结果摘要，详情再获取安全字段。

调用成功、认证拒绝、网络/工具/范围拒绝、参数错误、限流、业务失败、超时、取消和进程中断分开。JSON-RPC 即使 HTTP200 返回错误，也不能记作成功；`isError=true` 的工具结果同样标记失败。OAuth 授权/令牌/撤销以 `oauth/authorize`、`oauth/token`、`oauth/revoke` 登记；标准撤销成功表示请求被处理，不能推断被提交 token 是否曾存在。

普通 MCP 调用与成功 OAuth 事件完整记录。匿名前置限流按每 IP 每分钟最多一条 `transport/rate-limit` 记录；OAuth 拒绝最多十条/分钟/IP，后续拒绝不无限写盘。因此审计分页数量不能当作未经采样的攻击请求总数。CORS OPTIONS 为协议预检，不计作业务工具调用。

不记录 Authorization、Cookie、token、client secret、授权码、设备/SMTP凭据、任意参数或结果正文。请求标识仅保存 SHA256 摘要，平台调用 ID 与 TraceId 用于关联；参数仅保留已审核的设备/时间/分页摘要，其他文本只记“已提供”。配置变更审计保存前后版本及脱敏差异，秘密轮换不保存秘密。

调用开始至少写入数据库或受保护仓库外 JSONL 缓冲才准入。缓冲默认位于系统 LocalApplicationData 下 `OutOfBandManagement/mcp-audit/<EnvironmentName>`，默认上限 64 MiB；重放按调用 ID 幂等，启动前遗留 Started 标记 Interrupted，不自动删除缓冲或历史记录。数据库及缓冲均不可持久化时返回 `MCP_AUDIT_UNAVAILABLE`，不继续业务查询。

## 错误和排查

| 现象 | 优先核对 |
| --- | --- |
| 401 / MCP_UNAUTHORIZED | token 有效期、issuer/audience/resource、用户状态、安全版本及撤销；不回退 Cookie |
| 403 / MCP_HOST_DENIED、MCP_NETWORK_DENIED、MCP_ORIGIN_DENIED | 规范公开 Host、可信代理、实际源 IP/CIDR、精确 Origin |
| 工具不可见或 MCP_TOOL_DENIED | 全局工具、客户端工具、原同意快照及绑定用户当前业务权限的交集 |
| MCP_RESOURCE_INACCESSIBLE | 指定设备范围或设备确实不存在；不据此猜测其他设备 |
| MCP_QUERY_LIMIT / MCP_RESPONSE_TOO_LARGE | 缩短窗口、减少设备/指标或调整已获准限额，不自动截断 |
| 429 / MCP_RATE_LIMITED | 当前调用方或匿名前置限额；尊重 Retry-After，避免并发重试 |
| 503 / MCP_DISABLED、MCP_UNAVAILABLE | 服务开关、支持状态、HTTPS URL、证书、两套迁移及策略存储 |
| MCP_POLICY_REVOKED / MCP_TIMEOUT | 查询期间策略改变或超时；保留失败状态，不显示成功空数据 |
| 409 / MCP_VERSION_CONFLICT | 重新读取配置/调用方版本，审查后显式提交 |

关闭 MCP 不影响管理日志查询、普通 PC、后台采集或邮件。生产恢复先关闭服务，保留审计及增量表；回退程序前核对结构兼容性。OAuth 数据、签名/加密材料和撤销状态应一起纳入获准备份恢复，不能只恢复业务库后宣称授权状态已恢复。

## 桌面和合同边界

桌面版本期不支持外部 MCP，强制 `Mcp:Supported=false`，不开放动态回环端口、不绕过桌面 Gate、LAN 介质入口不暴露 MCP/OAuth/发现路径。共享业务表仍按 SQLite 迁移链升级；后续本地 MCP 需要独立设计和授权。

Swagger/OpenAPI 运行模块、UI、模板插件和旧生成链已退役，旧路径真实 404。REST 合同使用[路由清单与 JSON Schema](../oob/README.md)，PC 类型继续同源生成；MCP 工具 Schema 与 REST 合同分别维护。离线导出不读取上述私有配置，不运行 OAuth、采集、业务宿主或数据库。

参考：[MCP 授权规范](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization)、[MCP Tools](https://modelcontextprotocol.io/specification/2026-07-28/server/tools)、[官方 C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)、[OpenIddict 集成](https://documentation.openiddict.com/integrations/aspnet-core.html)。

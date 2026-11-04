# 独立离线合同工具

从仓库根使用正式命令：

```sh
node scripts/oob-contracts.mjs generate
node scripts/oob-contracts.mjs check
```

工具仅创建独立的 MVC 元数据服务容器，加载正式 Controller 类型，通过 ApiExplorer 与 EndpointDataSource 导出全部控制器路由；被 IgnoreApi 隐藏的控制台、介质流入口也进入覆盖清单。不调用 `OobApplication.RunAsync`，不启动监听或任何后台任务，不加载私密配置，不解析业务服务，不连接数据库、SMTP 或带外设备。

`routes.json` 保存方法、路径、参数位置、认证/CSRF、请求大小、响应和特殊流协议。健康、SignalR、MCP 和 OAuth 地址引用 API 的正式 `OobProtocolRoutes` 常量；组件自动发现地址单独登记。非 MVC 的声明不替代真实 HTTP/协议测试。`schemas.json` 从正式 `JsonStorage.Options` 和 .NET JsonSchemaExporter 生成，并显式保留项目 long 字符串、字符串枚举、响应必有但可空、请求校验、递归硬件和字典可空值。

Schema 使用已审核的 draft-07 兼容子集，遇到未适配关键字明确失败。ServiceResult 的动态状态码、设备能力、业务校验和副作用无法由反射证明，继续在[接口说明](../../../docs/api/oob/README.md)和服务测试中维护。闭合泛型无法恢复调用位置的 NRT 标注；当前泛型集合遵守正式非空项合同，专门验证分页和历史结果。

父前端锁定 `json-schema-to-typescript 16.0.0`，通过其本地程序 API 生成 PC 的 `generated.ts`。`components.schemas` 是原有 PC 的类型兼容出口，系统不提供 Swagger/OpenAPI 服务。工具核验长 ID、非法枚举、缺测值、递归子项、泛型分页和必填响应的 TypeScript 消费语义；check 在仓库外生成并对三份正式制品逐字比较，不覆盖或删除文件。

仅需调试导出时，可传仓库外的绝对输出目录：

```sh
dotnet run --project backend/tools/OobContracts/OobContracts.csproj -- /tmp/oob-contract-debug
```

该命令只输出路由与 Schema，正式 TypeScript 和漂移检查仍由上面的 Node 入口负责。

# 带外设备只读复验工具

从仓库根运行：

```sh
dotnet run --project backend/tools/OobReadProbe/OobReadProbe.csproj
dotnet run --project backend/tools/OobReadProbe/OobReadProbe.csproj -- --controller iLO4 --telemetry-only
dotnet run --project backend/tools/OobReadProbe/OobReadProbe.csproj -- --controller iLO4 --connection-only
dotnet run --project backend/tools/OobReadProbe/OobReadProbe.csproj -- --controller iLO4 --power-only
dotnet run --project backend/tools/OobReadProbe/OobReadProbe.csproj -- --controller iDRAC8 --inventory-only --session-auth
```

工具读取被忽略的 `.local/oob/application.json` 中 `Bootstrap.Servers`，仅在内存使用连接参数和证书 SHA-256 指纹。逐台串行执行连接测试、静态库存和实时指标读取，每台最多 90 秒；不读取数据库，也不依据应用电源开关执行设备动作。

默认模式的独立 HTTP 处理器在网络发送前拒绝所有非 GET 请求。显式添加 `--session-auth` 时，一轮读取优先创建标准认证会话并复用 token，结束后注销本轮创建且同源路径可核验的会话；仅额外允许标准 Sessions 集合的认证 POST 和本轮响应归属 item 的 DELETE，最多一次 GET 401 认证刷新。所有模式都拒绝电源、固件和设备配置写请求，不依据应用电源开关放行。证书策略与应用一致：显式指纹始终优先匹配；未配置指纹时，IgnoreCertificateErrors 默认 true，仅本连接忽略证书错误，设为 false 后按系统证书链校验。

当前生产兼容策略仅对 iDRAC8 启用会话复用；iLO4 即使设置此选项仍使用已验证稳定的 Basic 兼容读取，不创建认证会话。

标准输出仅含设备型号、固件、电源状态、硬件类别和控制器磁盘数量、指标类别/单位/可用性及安全提示；不输出账号、密码、认证头、完整配置或原始设备报文。执行日志应重定向至仓库外。

`--controller iLO4` 或 `--controller iDRAC8` 可以筛选设备，`--telemetry-only` 只复验实时读取，`--inventory-only` 只读取一次库存并报告总耗时。排查厂商字段兼容时，可显式设置进程变量 `OOB_READ_PROBE_SHAPES=1`；此时默认 GET 模式标准错误额外输出风扇属性名称、类型及读数/单位字段，仍不输出完整设备报文。

轻量网络诊断使用 `--transport-check` 对比系统代理默认行为与本客户端直连，或 `--connection-reuse-check` 在同一客户端连续读取服务根两次。两种模式均只做两个 GET，不遍历硬件；输出 HTTP 版本、响应头/正文/总耗时、连接关闭标记、代理是否绕过及相关环境变量是否存在，不输出代理 URL、认证内容或环境变量值。

`--session-auth-check` 专门对照认证耗时：GET 发现会话服务，创建一次临时认证会话，用 token 连续 GET 服务根两次，最后只注销本次已核验会话。该模式的本机白名单也禁止所有设备动作，只输出状态码、耗时和 token/归属是否存在，不输出 token 内容。没有可核验 Location 时不猜测删除目标，需由设备会话超时回收。

本工具只证明当前只读连通与解析结果。电源操作必须由用户按实际维护窗口自行验证，不能从只读通过推导开关机或重启已验收。

`--connection-only` 只进行连接和能力读取，输出型号、固件、当前电源状态及正式支持的动作；不遍历完整库存，也不执行任何电源命令。

`--power-only` 与电源页及发送前预检使用同一专用只读入口，仅发现唯一主机和必要动作说明，不读取管理器、机箱或遥测；iLO4 整轮关闭连接。它保留读取失败语义，不能把失败当作设备不支持；不会发送电源动作，也不能将读取成功等同于开关机验收。HTTP 只读守卫始终保持生效。

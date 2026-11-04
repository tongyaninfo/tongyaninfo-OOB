using OobContracts;

// 只组装 MVC 描述元数据，不调用业务宿主，不加载应用配置或启动监听。
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
    throw new ArgumentException("用法：OobContracts <绝对输出目录>");
await ContractExporter.ExportAsync(args[0]);

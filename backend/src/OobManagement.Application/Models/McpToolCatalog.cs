using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OobManagement.Application.Models;

/// <summary>唯一工具目录；描述、Schema 与查询分派共用固定名称。</summary>
public static class McpToolCatalog
{
    private static readonly JsonSerializerOptions Json = CreateJson();
    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
    private static object Text(string description) => new { type = "string", description, maxLength = 100 };
    private static object Integer(int minimum, int maximum) => new { type = "integer", minimum, maximum };
    private static McpToolDto Tool(string name, string description, string? permission, string[] required, params (string Key, object Schema)[] fields)
    {
        var properties = fields.ToDictionary(x => x.Key, x => x.Schema);
        return new(name, description, permission, false, true,
            JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false }, Json),
            JsonSerializer.SerializeToElement(Json.GetJsonSchemaAsNode(typeof(McpToolResult<>).MakeGenericType(OutputType(name)), new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true }), Json));
    }
    private static readonly (string, object)[] Window = [("from", Text("带时区的开始时间")), ("to", Text("带时区的结束时间"))];
    public static IReadOnlyList<McpToolDto> All { get; } = [
        Tool("oob_list_servers", "分页查询获准设备的安全基础信息，不访问带外设备", null, [],
            ("keyword", Text("名称关键词")), ("brand", Text("HP或DELL")), ("current", Integer(1,1000000)), ("pageSize",Integer(1,100))),
        Tool("oob_get_server", "查询已保存的设备状态，不返回管理账号和地址", null, ["serverId"], ("serverId",Text("设备ID字符串"))),
        Tool("oob_get_inventory", "查询已保存的硬件库存快照；不刷新真机", null, ["serverId"], ("serverId",Text("设备ID字符串")), ("category",Text("硬件类别"))),
        Tool("oob_get_latest_telemetry", "查询已保存的最新读数、质量和陈旧状态；不主动采样", null, ["serverId"], ("serverId",Text("设备ID字符串")), ("metricCode",Text("指标编码"))),
        Tool("oob_query_history", "查询最多七天历史聚合，保留单位和缺测，不补造数据", null, ["serverId","from","to"],
            ("serverId",Text("设备ID字符串")), Window[0],Window[1],("metricCode",Text("指标编码")),("bucketSeconds",Integer(1,604800)),("maxPoints",Integer(1,2000))),
        Tool("oob_get_overview", "统计获准范围内设备状态和最多七天运行趋势", null, [], Window[0],Window[1],("metricCode",Text("指标编码"))),
        Tool("oob_list_exceptions", "分页查询安全故障摘要；仅管理员且明确授予全部设备范围", "Administrator", [],
            Window[0],Window[1],("source",Text("来源模块")),("kind",Text("故障类别")),("current",Integer(1,1000000)),("pageSize",Integer(1,100))),
        Tool("oob_get_exception", "查询安全故障详情，不返回原始堆栈和请求正文", "Administrator", ["exceptionId"], ("exceptionId",Text("异常ID字符串"))),
        Tool("oob_list_power_operations", "仅查询电源操作历史，不执行设备动作或人工核对", "PowerOperations", ["serverId"],
            ("serverId",Text("设备ID字符串")), Window[0],Window[1],("status",Text("操作状态")),("current",Integer(1,1000000)),("pageSize",Integer(1,100))),
        Tool("oob_preview_operation_report", "按获准设备生成最多31天结构化采集和传感器统计，不发送邮件", "Administrator", ["from","to"],
            Window[0],Window[1],("period",Text("day、week或month")))
    ];
    public static bool Contains(string name) => All.Any(x => x.Name == name);
    private static Type OutputType(string name) => name switch
    {
        "oob_list_servers" => typeof(PageData<McpServerInfoDto>), "oob_get_server" => typeof(McpServerInfoDto),
        "oob_get_inventory" => typeof(McpInventoryDto), "oob_get_latest_telemetry" => typeof(McpTelemetryDto),
        "oob_query_history" => typeof(McpHistoryDto), "oob_get_overview" => typeof(McpOverviewDto),
        "oob_list_exceptions" => typeof(PageData<McpExceptionDto>), "oob_get_exception" => typeof(McpExceptionDto),
        "oob_list_power_operations" => typeof(PageData<PowerOperationDto>), "oob_preview_operation_report" => typeof(McpOperationReportDto),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}

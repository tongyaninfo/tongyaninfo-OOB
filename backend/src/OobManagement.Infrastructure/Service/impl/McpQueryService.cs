using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>仅访问已保存数据；聚合前在SQL加入设备范围，完全不依赖带外驱动或SMTP。</summary>
public sealed class McpQueryService(IDbContextFactory<OobDbContext> factory, IMcpPolicyService policy,
    IServerManagementService servers, TimeProvider clock) : IMcpQueryService
{
    public async Task<ServiceResult<JsonElement>> ExecuteAsync(McpAccessContext context, string tool, JsonElement arguments, CancellationToken ct)
    {
        var descriptor = McpToolCatalog.All.FirstOrDefault(x => x.Name == tool);
        if (descriptor is null) return Fail("MCP_TOOL_DENIED", "工具未获准", 403);
        if (!Validate(descriptor.InputSchema, arguments)) return Fail("MCP_INVALID_ARGUMENTS", "工具参数不符合输入Schema", 400);
        try
        {
            long? serverId = arguments.TryGetProperty("serverId",out var sid) ? McpRules.Id(sid.GetString(),out var parsed) ? parsed : throw new ArgumentException() : null;
            var request = context.Request with { Tool = tool, ServerId = serverId };
            var before = await policy.AuthorizeAsync(request,ct);
            if (!before.Success) return Fail(before.ErrorCode!,before.ErrorMessage!,before.StatusCode);
            await using var db = await factory.CreateDbContextAsync(ct);
            var scope = before.Data!;
            ServiceResult<JsonElement> result = tool switch
            {
                "oob_list_servers" => await ListServers(db,scope,arguments,ct),
                "oob_get_server" => await GetServer(db,scope,serverId!.Value,ct),
                "oob_get_inventory" => await Inventory(db,scope,serverId!.Value,arguments,ct),
                "oob_get_latest_telemetry" => await Latest(db,scope,serverId!.Value,arguments,ct),
                "oob_query_history" => await History(db,scope,serverId!.Value,arguments,ct),
                "oob_get_overview" => await Overview(db,scope,arguments,ct),
                "oob_list_exceptions" => await Exceptions(db,arguments,ct),
                "oob_get_exception" => await Exception(db,arguments,ct),
                "oob_list_power_operations" => await Power(db,scope,serverId!.Value,arguments,ct),
                "oob_preview_operation_report" => await Report(db,scope,arguments,ct),
                _ => Fail("MCP_TOOL_DENIED","工具未获准",403)
            };
            var after = await policy.AuthorizeAsync(request,ct);
            if (!after.Success) return Fail(after.ErrorCode!,after.ErrorMessage!,after.StatusCode);
            if (scope.AllServers && !after.Data!.AllServers || !scope.AllServers && !after.Data!.AllServers && scope.ServerIds.Except(after.Data.ServerIds).Any())
                return Fail("MCP_POLICY_CHANGED","查询期间授权范围已缩小，请重新查询",403);
            return result;
        }
        catch (Exception error) when (error is ArgumentException or FormatException)
        { return Fail("MCP_INVALID_ARGUMENTS","工具参数无法按合同读取",400); }
        catch (JsonException)
        { return Fail("MCP_SAVED_DATA_UNAVAILABLE","保存数据无法按工具合同读取",503); }
    }
    private static IQueryable<ManagedServer> Scope(OobDbContext db,McpAccessContext ctx) => db.Servers.AsNoTracking().Where(x => ctx.AllServers || ctx.ServerIds.Contains(x.Id));
    private static McpServerInfoDto Server(ManagedServer x) => new(x.Id.ToString(),x.Name,x.Brand,x.ControllerType,x.Enabled,
        x.ConnectionStatus,x.LastCollectedAt,x.LastInventoryAt,JsonStorage.Read<DeviceIdentity>(x.IdentityJson) ?? new());
    private static string? Text(JsonElement args,string key) => args.TryGetProperty(key,out var value) ? value.GetString() : null;
    private static int Number(JsonElement args,string key,int fallback) => args.TryGetProperty(key,out var value) ? value.GetInt32() : fallback;
    private static (int Current,int Size) Page(JsonElement args) => (Number(args,"current",1),Number(args,"pageSize",20));
    private (DateTimeOffset From,DateTimeOffset To) Window(JsonElement args,int maxDays,int defaultMinutes = 15)
    {
        var fromText = Text(args,"from"); var toText = Text(args,"to");
        if ((fromText is null)!=(toText is null)) throw new ArgumentException();
        var to = toText is null ? clock.GetUtcNow() : DateTimeOffset.Parse(toText).ToUniversalTime();
        var from = fromText is null ? to.AddMinutes(-defaultMinutes) : DateTimeOffset.Parse(fromText).ToUniversalTime();
        if (from>=to || to-from>TimeSpan.FromDays(maxDays) || fromText is not null && (!HasZone(fromText)||!HasZone(toText!))) throw new ArgumentException();
        return (from,to);
    }
    private static bool HasZone(string text) => text.EndsWith('Z') || text.Length>=6 && (text[^6]=='+'||text[^6]=='-') && text[^3]==':';
    private static ServiceResult<JsonElement> Ok<T>(T data) => ServiceResult<JsonElement>.Ok(JsonSerializer.SerializeToElement(new McpToolResult<T>("1",data),JsonStorage.Options));
    private static ServiceResult<JsonElement> Fail(string code,string message,int status = 400) => ServiceResult<JsonElement>.Fail(code,message,status);
    private static ServiceResult<JsonElement> Missing() => Fail("MCP_RESOURCE_INACCESSIBLE","目标不可访问",404);
    private static ServiceResult<JsonElement> TooLarge() => Fail("MCP_QUERY_LIMIT","查询超过工具上限，请缩小设备、指标或时间范围",422);
    private static async Task<ServiceResult<JsonElement>> ListServers(OobDbContext db,McpAccessContext ctx,JsonElement args,CancellationToken ct)
    {
        var rows=Scope(db,ctx); var keyword=Text(args,"keyword"); var brand=Text(args,"brand");
        if (keyword is not null) rows=rows.Where(x=>x.Name.Contains(keyword));
        if (brand is not null) rows=rows.Where(x=>x.Brand==brand);
        var total=await rows.CountAsync(ct); var(current,size)=Page(args);
        var page=await rows.OrderBy(x=>x.Name).ThenBy(x=>x.Id).Skip((current-1)*size).Take(size).ToArrayAsync(ct);
        return Ok(new PageData<McpServerInfoDto>(page.Select(Server).ToArray(),total,current,size));
    }
    private static async Task<ServiceResult<JsonElement>> GetServer(OobDbContext db,McpAccessContext ctx,long id,CancellationToken ct)
    {
        var row=await Scope(db,ctx).SingleOrDefaultAsync(x=>x.Id==id,ct); return row is null ? Missing() : Ok(Server(row));
    }
    private static async Task<ServiceResult<JsonElement>> Inventory(OobDbContext db,McpAccessContext ctx,long id,JsonElement args,CancellationToken ct)
    {
        if (!await Scope(db,ctx).AnyAsync(x=>x.Id==id,ct)) return Missing();
        var row=await db.Inventories.AsNoTracking().Where(x=>x.ServerId==id).OrderByDescending(x=>x.CollectedAt).ThenByDescending(x=>x.Id).FirstOrDefaultAsync(ct);
        var inventory=row is null ? null : JsonStorage.Read<DeviceInventory>(row.PayloadJson);
        var components=inventory?.Components.Select(x=>Hardware(x,Text(args,"category"))).Where(x=>x is not null).Cast<McpHardwareDto>().ToArray() ?? [];
        return Ok(new McpInventoryDto(id.ToString(),inventory is null ? "NotCollected" : "Saved",inventory?.CollectedAt,"SavedInventory",components));
    }
    private static McpHardwareDto? Hardware(HardwareComponent h,string? category)
    {
        var children=h.Children.Select(x=>Hardware(x,category)).Where(x=>x is not null).Cast<McpHardwareDto>().ToArray();
        if(category is not null && !string.Equals(category,h.Kind,StringComparison.OrdinalIgnoreCase) && children.Length==0) return null;
        return new(h.Key,h.Kind,h.Name,h.Model,h.Manufacturer,h.SerialNumber,h.Status,h.CapacityBytes?.ToString(),children);
    }
    private async Task<ServiceResult<JsonElement>> Latest(OobDbContext db,McpAccessContext ctx,long id,JsonElement args,CancellationToken ct)
    {
        var row=await Scope(db,ctx).SingleOrDefaultAsync(x=>x.Id==id,ct); if(row is null) return Missing();
        var reading=JsonStorage.Read<TelemetryReading>(row.LatestTelemetryJson) ?? new(); var code=Text(args,"metricCode");
        var stale=row.ConnectionStatus!="Online"||row.LastCollectedAt is null||clock.GetUtcNow()-row.LastCollectedAt>TimeSpan.FromSeconds(Math.Max(row.SamplingIntervalSeconds*3,60));
        return Ok(new McpTelemetryDto(id.ToString(),row.LastCollectedAt,stale,reading.Metrics.Where(x=>code is null||x.MetricCode==code).ToArray()));
    }
    private async Task<ServiceResult<JsonElement>> History(OobDbContext db,McpAccessContext ctx,long id,JsonElement args,CancellationToken ct)
    {
        if(!await Scope(db,ctx).AnyAsync(x=>x.Id==id,ct)) return Missing();
        var(from,to)=Window(args,7);var max=Number(args,"maxPoints",1000);
        var bucket=Math.Max(Number(args,"bucketSeconds",1),(int)Math.Ceiling((to-from).TotalSeconds/Math.Max(1,max-1)));
        var code=Text(args,"metricCode");
        if(await db.Series.CountAsync(x=>x.ServerId==id&&(code==null||x.MetricCode==code),ct)>256) return TooLarge();
        var history=await servers.HistoryAsync(id,from,to,code,bucket,ct);
        if(!history.Success) return Fail(history.ErrorCode!,history.ErrorMessage!,history.StatusCode);
        if(history.Data!.Any(x=>x.Points.Count>max)) return TooLarge();
        return Ok(new McpHistoryDto(id.ToString(),from,to,bucket,"PersistedAggregate",await HistorySources(db,id,from,to,history.Data!,ct)));
    }
    private async Task<ServiceResult<JsonElement>> Overview(OobDbContext db,McpAccessContext ctx,JsonElement args,CancellationToken ct)
    {
        var(from,to)=Window(args,7); var rows=await Scope(db,ctx).OrderBy(x=>x.Id).Take(65).ToArrayAsync(ct);
        if(rows.Length>64) return TooLarge();
        var ids=rows.Select(x=>x.Id).ToArray(); var code=Text(args,"metricCode");
        if(await db.Series.CountAsync(x=>ids.Contains(x.ServerId)&&(code==null||x.MetricCode==code),ct)>256) return TooLarge();
        var trends=new List<McpHistoryDto>();var bucket=Math.Max(1,(int)Math.Ceiling((to-from).TotalSeconds/60));
        foreach(var row in rows)
        {
            var result=await servers.HistoryAsync(row.Id,from,to,code,bucket,ct);
            if(!result.Success)return Fail(result.ErrorCode!,result.ErrorMessage!,result.StatusCode);
            trends.Add(new(row.Id.ToString(),from,to,bucket,"PersistedAggregate",await HistorySources(db,row.Id,from,to,result.Data!,ct)));
        }
        return Ok(new McpOverviewDto(rows.Length,rows.Count(x=>x.ConnectionStatus=="Online"),rows.Count(x=>x.ConnectionStatus=="Offline"),
            rows.Count(x=>x.ConnectionStatus=="Pending"),from,to,rows.Select(Server).ToArray(),trends));
    }
    private static async Task<IReadOnlyList<McpHistorySeriesDto>> HistorySources(OobDbContext db,long serverId,DateTimeOffset from,
        DateTimeOffset to,IReadOnlyList<HistorySeriesDto> series,CancellationToken ct)
    {
        var sources=await db.Samples.AsNoTracking().Where(s=>s.SampledAt>=from&&s.SampledAt<to)
            .Join(db.Series.Where(s=>s.ServerId==serverId),s=>s.SeriesId,s=>s.Id,(sample,sensor)=>new{sensor.SensorKey,sensor.MetricCode,sample.Source})
            .Distinct().ToArrayAsync(ct);
        return series.Select(s=>new McpHistorySeriesDto(s.SensorKey,s.Name,s.MetricCode,s.Unit,
            sources.Where(x=>x.SensorKey==s.SensorKey&&x.MetricCode==s.MetricCode).Select(x=>x.Source).Order().ToArray(),s.Points)).ToArray();
    }
    private static McpExceptionDto Fault(SystemExceptionRecord x)=>new(x.Id.ToString(),x.OccurredAt,x.Kind,x.Level,x.Source,x.Operation,x.ErrorCode,x.Message,x.ServerId?.ToString(),x.TraceId);
    private async Task<ServiceResult<JsonElement>> Exceptions(OobDbContext db,JsonElement args,CancellationToken ct)
    {
        var(from,to)=Window(args,31,1440);var rows=db.ExceptionLogs.AsNoTracking().Where(x=>x.OccurredAt>=from&&x.OccurredAt<to);
        var source=Text(args,"source");var kind=Text(args,"kind");
        if(source is not null)rows=rows.Where(x=>x.Source==source); if(kind is not null)rows=rows.Where(x=>x.Kind==kind);
        var total=await rows.CountAsync(ct);var(current,size)=Page(args);
        var data=await rows.OrderByDescending(x=>x.OccurredAt).ThenByDescending(x=>x.Id).Skip((current-1)*size).Take(size).ToArrayAsync(ct);
        return Ok(new PageData<McpExceptionDto>(data.Select(Fault).ToArray(),total,current,size));
    }
    private static async Task<ServiceResult<JsonElement>> Exception(OobDbContext db,JsonElement args,CancellationToken ct)
    {
        if(!McpRules.Id(Text(args,"exceptionId"),out var id))throw new ArgumentException();
        var row=await db.ExceptionLogs.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);return row is null?Missing():Ok(Fault(row));
    }
    private async Task<ServiceResult<JsonElement>> Power(OobDbContext db,McpAccessContext ctx,long id,JsonElement args,CancellationToken ct)
    {
        if(!await Scope(db,ctx).AnyAsync(x=>x.Id==id,ct))return Missing();
        var(from,to)=Window(args,31,1440);var status=Text(args,"status");
        var rows=db.PowerOperations.AsNoTracking().Where(x=>x.ServerId==id&&x.CreatedAt>=from&&x.CreatedAt<to);
        if(status is not null)rows=rows.Where(x=>x.Status==status);
        var total=await rows.CountAsync(ct);var(current,size)=Page(args);
        var data=await rows.OrderByDescending(x=>x.CreatedAt).ThenByDescending(x=>x.Id).Skip((current-1)*size).Take(size).ToArrayAsync(ct);
        return Ok(new PageData<PowerOperationDto>(data.Select(x=>new PowerOperationDto(x.Id.ToString(),x.RequestId,Enum.Parse<PowerAction>(x.Action),x.Status,x.Message,x.CreatedAt,x.CompletedAt)).ToArray(),total,current,size));
    }
    private async Task<ServiceResult<JsonElement>> Report(OobDbContext db,McpAccessContext ctx,JsonElement args,CancellationToken ct)
    {
        var(from,to)=Window(args,31);var period=Text(args,"period")??"month";
        if(period is not("day" or "week" or "month"))throw new ArgumentException();
        var allowed=Scope(db,ctx).Where(x=>x.CreatedAt<to);
        var rows=await allowed.OrderBy(x=>x.Id).Take(1001).ToArrayAsync(ct);if(rows.Length>1000)return TooLarge();
        var allowedIds=allowed.Select(x=>x.Id);
        var polls=await db.PollRuns.AsNoTracking().Where(x=>allowedIds.Contains(x.ServerId)&&x.StartedAt>=from&&x.StartedAt<to)
            .GroupBy(x=>x.ServerId).Select(g=>new{Id=g.Key,Total=g.Count(),Successful=g.Count(x=>x.Status=="Success"),Failed=g.Count(x=>x.Status=="Failed")}).ToDictionaryAsync(x=>x.Id,ct);
        var faults=await db.ExceptionLogs.AsNoTracking().Where(x=>x.ServerId!=null&&allowedIds.Contains(x.ServerId.Value)&&x.OccurredAt>=from&&x.OccurredAt<to)
            .GroupBy(x=>x.ServerId!.Value).Select(g=>new{Id=g.Key,Count=g.Count()}).ToDictionaryAsync(x=>x.Id,ct);
        var sensors=db.Series.AsNoTracking().Where(x=>allowedIds.Contains(x.ServerId));
        var statistics=await db.Samples.AsNoTracking().Where(x=>x.SampledAt>=from&&x.SampledAt<to&&x.Availability=="Available"&&x.Value!=null&&x.Value>=-double.MaxValue&&x.Value<=double.MaxValue)
            .Join(sensors,x=>x.SeriesId,s=>s.Id,(x,s)=>new{Sample=x,Sensor=s})
            .Where(x=>(x.Sensor.MetricCode=="temperature.celsius"&&x.Sensor.Unit=="°C")
                ||(x.Sensor.MetricCode=="power.watts"&&x.Sensor.Unit=="W"&&x.Sample.Value>=0)
                ||(x.Sensor.MetricCode=="fan.rpm"&&x.Sensor.Unit=="RPM"&&x.Sample.Value>=0)
                ||(x.Sensor.MetricCode=="fan.percent"&&x.Sensor.Unit=="%"&&x.Sample.Value>=0&&x.Sample.Value<=100))
            .GroupBy(x=>new{x.Sensor.ServerId,x.Sensor.SensorKey,x.Sensor.Name,x.Sensor.MetricCode,x.Sensor.Unit,x.Sample.Source})
            .Select(g=>new{g.Key.ServerId,g.Key.SensorKey,g.Key.Name,g.Key.MetricCode,g.Key.Unit,g.Key.Source,Count=g.LongCount(),Average=g.Average(x=>x.Sample.Value),Minimum=g.Min(x=>x.Sample.Value),Maximum=g.Max(x=>x.Sample.Value)})
            .OrderBy(x=>x.ServerId).ThenBy(x=>x.SensorKey).ThenBy(x=>x.MetricCode).ThenBy(x=>x.Source).Take(4097).ToArrayAsync(ct);
        if(statistics.Length>4096)return TooLarge();
        return Ok(new McpOperationReportDto(from,to,period,rows.Select(s=>new McpPollStatisticsDto(s.Id.ToString(),s.Name,
            polls.GetValueOrDefault(s.Id)?.Total??0,polls.GetValueOrDefault(s.Id)?.Successful??0,polls.GetValueOrDefault(s.Id)?.Failed??0,faults.GetValueOrDefault(s.Id)?.Count??0)).ToArray(),
            statistics.Select(x=>new McpSensorStatisticsDto(x.ServerId.ToString(),x.SensorKey,x.Name,x.MetricCode,x.Unit,x.Source,x.Count.ToString(),x.Average,x.Minimum,x.Maximum)).ToArray(),
            "按显式UTC左闭右开窗口统计已入库轮次与有效原始样本。采集成功率不等于在线率；period为报告类型标签，不改变调用方提供的窗口；不含范围外设备或无设备归属的全局异常。"));
    }
    private static bool Validate(JsonElement schema,JsonElement args)
    {
        if(args.ValueKind!=JsonValueKind.Object)return false;
        var properties=schema.GetProperty("properties");
        foreach(var required in schema.GetProperty("required").EnumerateArray())if(!args.TryGetProperty(required.GetString()!,out _))return false;
        foreach(var p in args.EnumerateObject())
        {
            if(!properties.TryGetProperty(p.Name,out var definition))return false;
            if(definition.GetProperty("type").GetString()=="string")
            {if(p.Value.ValueKind!=JsonValueKind.String||p.Value.GetString()!.Length>definition.GetProperty("maxLength").GetInt32())return false;}
            else if(p.Value.ValueKind!=JsonValueKind.Number||!p.Value.TryGetInt32(out var n)||n<definition.GetProperty("minimum").GetInt32()||n>definition.GetProperty("maximum").GetInt32())return false;
        }
        return true;
    }
}

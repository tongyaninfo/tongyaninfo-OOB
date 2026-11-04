using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>开始审计至少写入数据库或受保护日志才准入；追加缓冲不自动删除，重放按调用ID合并。</summary>
public sealed class McpAuditService(IDbContextFactory<OobDbContext> factory, IIdService ids, McpAuditOptions options,
    TimeProvider clock) : IMcpAuditService
{
    private readonly ConcurrentDictionary<string, McpCallLog> _pending = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly DateTimeOffset _boot = clock.GetUtcNow();
    private long _replayedOffset;
    private static readonly HashSet<string> Results = ["Started","Succeeded","AuthenticationFailed","NetworkDenied","ToolDenied","ScopeDenied","InvalidArguments","RateLimited","BusinessFailed","TimedOut","Canceled","Interrupted","ServiceDisabled","SystemFailed"];
    public async Task<ServiceResult<string>> StartAsync(McpCallStart request, CancellationToken ct)
    {
        var id = ids.NextId();
        var row = new McpCallLog
        {
            Id = id, CallId = id.ToString(CultureInfo.InvariantCulture), OccurredAt = clock.GetUtcNow(),
            ClientId = McpRules.Id(request.ClientId, out var client) ? client : null,
            UserId = McpRules.Id(request.UserId, out var user) ? user : null,
            ServerId = McpRules.Id(request.ServerId, out var server) ? server : null,
            ClientName = request.ClientId is null ? null : McpRules.Safe(request.ClientName,100),
            Method = request.Method is "server/discover" or "initialize" or "tools/list" or "tools/call" or "ping" or "notifications/initialized" or "notifications/cancelled"
                or "oauth/authorize" or "oauth/token" or "oauth/revoke" or "transport/rate-limit" ? request.Method : "UnknownMethod",
            Tool = request.Tool is not null && McpToolCatalog.Contains(request.Tool) ? request.Tool : null,
            SourceIp = System.Net.IPAddress.TryParse(request.SourceIp, out var ip) ? ip.ToString() : "Unknown",
            ProtocolVersion = request.ProtocolVersion is "2026-07-28" or "2025-11-25" ? request.ProtocolVersion : null,
            TraceId = Identifier(request.TraceId,128), RequestId = request.RequestId is null ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.RequestId))),
            ParametersSummary = ParameterSummary(request.Arguments), PolicySummary = PolicySummary(request.PolicySummary)
        };
        if (!await Persist(row, ct) && !await Append(row, ct))
            return ServiceResult<string>.Fail("MCP_AUDIT_UNAVAILABLE", "调用审计无法持久化", 503);
        _pending[row.CallId] = row;
        return ServiceResult<string>.Ok(row.CallId);
    }
    public async Task CompleteAsync(string callId, McpCallCompletion result, CancellationToken ct)
    {
        if (!_pending.TryGetValue(callId, out var row))
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            row = await db.McpCallLogs.AsNoTracking().SingleOrDefaultAsync(x => x.CallId == callId, ct);
            if (row is null) return;
        }
        row.CompletedAt = clock.GetUtcNow(); row.Result = Results.Contains(result.Result) ? result.Result : "SystemFailed";
        row.ErrorCode = Identifier(result.ErrorCode,100); row.HttpStatusCode = result.HttpStatusCode is >= 100 and <= 599 ? result.HttpStatusCode : null;
        row.DurationMilliseconds = Math.Max(0,(long)(row.CompletedAt.Value-row.OccurredAt).TotalMilliseconds);
        // 不信任调用方转交的正文或错误文本；仅记录由审计器产生的结果摘要。
        row.OutputSummary = "执行结果=" + row.Result + (row.ErrorCode is null ? "" : "；错误码=" + row.ErrorCode);
        row.ExceptionId = McpRules.Id(result.ExceptionId,out var exception) ? exception : null;
        if (await Persist(row,ct) || await Append(row,ct)) _pending.TryRemove(callId,out _);
        else throw new IOException("MCP调用结果审计无法持久化");
    }
    private async Task<bool> Persist(McpCallLog row, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var existing = await db.McpCallLogs.SingleOrDefaultAsync(x => x.CallId == row.CallId,ct);
            if (existing is null) db.McpCallLogs.Add(Clone(row));
            else if (existing.CompletedAt is null || row.CompletedAt > existing.CompletedAt)
                db.Entry(existing).CurrentValues.SetValues(row);
            await db.SaveChangesAsync(ct);
            if (row.ClientId is { } id)
                await db.McpClients.Where(x => x.Id == id && (x.LastCalledAt == null || x.LastCalledAt < row.OccurredAt))
                    .ExecuteUpdateAsync(x => x.SetProperty(c => c.LastCalledAt,row.OccurredAt),ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }
    private static McpCallLog Clone(McpCallLog row) => JsonSerializer.Deserialize<McpCallLog>(JsonSerializer.Serialize(row,McpRules.Json),McpRules.Json)!;
    private async Task<bool> Append(McpCallLog row,CancellationToken ct)
    {
        await _io.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(options.SpoolDirectory) || !Path.IsPathFullyQualified(options.SpoolDirectory)) return false;
            Directory.CreateDirectory(options.SpoolDirectory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(options.SpoolDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            var path = Path.Combine(options.SpoolDirectory,"mcp-audit.jsonl");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row,McpRules.Json)+"\n");
            if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > options.MaxSpoolBytes) return false;
            var fileOptions=new FileStreamOptions { Mode=FileMode.OpenOrCreate,Access=FileAccess.ReadWrite,Share=FileShare.Read,
                Options=FileOptions.Asynchronous|FileOptions.WriteThrough };
            if(!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            await using var stream = new FileStream(path,fileOptions);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            // 强杀留下的半行保留原文并加分隔，后续完整审计不能被半行永久阻塞。
            if(stream.Length>0){stream.Seek(-1,SeekOrigin.End);if(stream.ReadByte()!='\n'){stream.Seek(0,SeekOrigin.End);stream.WriteByte((byte)'\n');}}
            stream.Seek(0,SeekOrigin.End);
            await stream.WriteAsync(bytes,ct); await stream.FlushAsync(ct); stream.Flush(true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        finally { _io.Release(); }
    }
    public async Task ReplayAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.SpoolDirectory))
        {
            await _io.WaitAsync(ct);
            try
            {
                var path = Path.Combine(options.SpoolDirectory,"mcp-audit.jsonl");
                if (File.Exists(path))
                {
                    await using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                    stream.Seek(_replayedOffset,SeekOrigin.Begin);
                    using var reader = new StreamReader(stream,Encoding.UTF8);
                    var consumed = _replayedOffset;
                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        var nextOffset=consumed+Encoding.UTF8.GetByteCount(line)+1;
                        if(nextOffset>stream.Length)break;
                        McpCallLog? row;
                        try { row = JsonSerializer.Deserialize<McpCallLog>(line,McpRules.Json); }
                        catch (JsonException) { row=null; }
                        if (row is not null && !await Persist(row,ct)) break;
                        consumed = nextOffset;
                        _replayedOffset = consumed;
                    }
                }
            }
            finally { _io.Release(); }
        }
        foreach(var pending in _pending.Where(x=>x.Value.CompletedAt is not null).ToArray())
            if(await Persist(pending.Value,ct))_pending.TryRemove(pending.Key,out _);
        await using var db = await factory.CreateDbContextAsync(ct);
        // 仅本进程启动前遗留的开始记录标记中断，不影响本进程仍在执行的调用。
        await db.McpCallLogs.Where(x => x.Result == "Started" && x.OccurredAt < _boot)
            .ExecuteUpdateAsync(x => x.SetProperty(r => r.Result,"Interrupted").SetProperty(r => r.OutputSummary,"进程中断，未确认完整结果"),ct);
    }
    private static string? Identifier(string? value,int max) => value is null ? null : new string(value.Where(x => char.IsAsciiLetterOrDigit(x)||x is '_' or '-' or ':' or '.').Take(max).ToArray());
    private static string? PolicySummary(string? value)
    {
        // 仅允许根守卫传递短的规则标识，不保存任意文本。
        return value is null ? null : Identifier(value,1000);
    }
    public static string ParameterSummary(JsonElement? arguments)
    {
        if (arguments is not { ValueKind:JsonValueKind.Object } args) return "{}";
        var values = new Dictionary<string,object?>();
        foreach(var p in args.EnumerateObject())
        {
            if (p.Name is "serverId" or "exceptionId" && p.Value.ValueKind==JsonValueKind.String && McpRules.Id(p.Value.GetString(),out var id)) values[p.Name]=id.ToString(CultureInfo.InvariantCulture);
            else if(p.Name is "current" or "pageSize" or "bucketSeconds" or "maxPoints" && p.Value.ValueKind==JsonValueKind.Number && p.Value.TryGetInt32(out var number)) values[p.Name]=number;
            else if(p.Name is "from" or "to" && p.Value.ValueKind==JsonValueKind.String && p.Value.TryGetDateTimeOffset(out var time)) values[p.Name]=time.ToUniversalTime();
            else if(p.Name is "keyword" or "brand" or "category" or "metricCode" or "period" or "source" or "kind" or "status") values[p.Name]="已提供";
        }
        return JsonSerializer.Serialize(values,McpRules.Json);
    }
}

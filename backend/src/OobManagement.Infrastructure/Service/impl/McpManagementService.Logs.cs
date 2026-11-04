using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Service.impl;

public sealed partial class McpManagementService
{
    private static McpCallLogDto LogView(McpCallLog r, bool detail) => new(r.Id.ToString(), r.CallId, r.OccurredAt, r.CompletedAt,
        r.ClientId?.ToString(), r.ClientName, r.UserId?.ToString(), r.Method, r.Tool, r.ServerId?.ToString(), r.SourceIp,
        r.ProtocolVersion, r.Result, r.ErrorCode, r.HttpStatusCode, r.DurationMilliseconds, r.TraceId, r.RequestId,
        detail ? r.ParametersSummary : null, detail ? r.OutputSummary : null, detail ? r.PolicySummary : null, r.ExceptionId?.ToString());
    public async Task<ServiceResult<PageData<McpCallLogDto>>> ListLogsAsync(UserSessionContext actor, McpLogQuery query, CancellationToken ct)
    {
        if (await Admin<PageData<McpCallLogDto>>(actor, ct) is { } rejected) return rejected;
        var to = (query.To ?? clock.GetUtcNow()).ToUniversalTime(); var from = (query.From ?? to.AddDays(-1)).ToUniversalTime();
        if (query.Current is < 1 or > 1000000 || query.PageSize is < 1 or > 100 || from >= to || to - from > TimeSpan.FromDays(31)
            || query.SortField is not ("occurredAt" or "durationMilliseconds") || query.SortOrder is not ("ascend" or "descend")
            || new[] { query.ClientId,query.UserId,query.Method,query.Tool,query.ServerId,query.SourceIp,query.Result,query.ErrorCode,query.TraceId,query.CallId }.Any(x => x?.Length > 128))
            return Invalid<PageData<McpCallLogDto>>("日志查询须为有效的31天内范围与分页");
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = db.McpCallLogs.AsNoTracking().Where(x => x.OccurredAt >= from && x.OccurredAt < to);
        if (query.ClientId is not null) { if (!McpRules.Id(query.ClientId, out var id)) return Invalid<PageData<McpCallLogDto>>(); rows = rows.Where(x => x.ClientId == id); }
        if (query.UserId is not null) { if (!McpRules.Id(query.UserId, out var id)) return Invalid<PageData<McpCallLogDto>>(); rows = rows.Where(x => x.UserId == id); }
        if (query.ServerId is not null) { if (!McpRules.Id(query.ServerId, out var id)) return Invalid<PageData<McpCallLogDto>>(); rows = rows.Where(x => x.ServerId == id); }
        if (!string.IsNullOrEmpty(query.Method)) rows = rows.Where(x => x.Method == query.Method);
        if (!string.IsNullOrEmpty(query.Tool)) rows = rows.Where(x => x.Tool == query.Tool);
        if (!string.IsNullOrEmpty(query.SourceIp)) rows = rows.Where(x => x.SourceIp == query.SourceIp);
        if (!string.IsNullOrEmpty(query.Result)) rows = rows.Where(x => x.Result == query.Result);
        if (!string.IsNullOrEmpty(query.ErrorCode)) rows = rows.Where(x => x.ErrorCode == query.ErrorCode);
        if (!string.IsNullOrEmpty(query.TraceId)) rows = rows.Where(x => x.TraceId == query.TraceId);
        if (!string.IsNullOrEmpty(query.CallId)) rows = rows.Where(x => x.CallId == query.CallId);
        var total = await rows.CountAsync(ct); var asc = query.SortOrder == "ascend";
        var sorted = query.SortField == "durationMilliseconds"
            ? asc ? rows.OrderBy(x => x.DurationMilliseconds) : rows.OrderByDescending(x => x.DurationMilliseconds)
            : asc ? rows.OrderBy(x => x.OccurredAt) : rows.OrderByDescending(x => x.OccurredAt);
        var items = await sorted.ThenByDescending(x => x.Id).Skip((query.Current - 1) * query.PageSize).Take(query.PageSize).ToArrayAsync(ct);
        return ServiceResult<PageData<McpCallLogDto>>.Ok(new(items.Select(x => LogView(x, false)).ToArray(), total, query.Current, query.PageSize));
    }
    public async Task<ServiceResult<McpCallLogDto>> GetLogAsync(UserSessionContext actor, long id, CancellationToken ct)
    {
        if (await Admin<McpCallLogDto>(actor, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.McpCallLogs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return row is null ? ServiceResult<McpCallLogDto>.Fail("MCP_LOG_NOT_FOUND", "调用记录不存在", 404) : ServiceResult<McpCallLogDto>.Ok(LogView(row, true));
    }
    public async Task<ServiceResult<PageData<McpConfigAuditDto>>> ListConfigurationAuditsAsync(UserSessionContext actor, int current, int pageSize, CancellationToken ct)
    {
        if (await Admin<PageData<McpConfigAuditDto>>(actor, ct) is { } rejected) return rejected;
        if (current is < 1 or > 1000000 || pageSize is < 1 or > 100) return Invalid<PageData<McpConfigAuditDto>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var total = await db.McpConfigAudits.CountAsync(ct);
        var rows = await db.McpConfigAudits.AsNoTracking().OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Skip((current - 1) * pageSize).Take(pageSize).ToArrayAsync(ct);
        return ServiceResult<PageData<McpConfigAuditDto>>.Ok(new(rows.Select(x => new McpConfigAuditDto(x.Id.ToString(), x.OccurredAt,
            x.UserId.ToString(), x.Action, x.ObjectId, x.PreviousVersion.ToString(), x.CurrentVersion.ToString(), x.Summary)).ToArray(), total, current, pageSize));
    }
}

using Microsoft.EntityFrameworkCore;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>检索只读异常记录；预期参数错误用服务结果返回，不以异常控制业务。</summary>
public sealed class ExceptionLogService(IDbContextFactory<OobDbContext> factory) : IExceptionLogService
{
    public async Task<ServiceResult<PageData<ExceptionLogDto>>> ListAsync(ExceptionLogQuery query, CancellationToken ct)
    {
        if (query.Current < 1 || query.Current > 1000000 || query.PageSize is < 1 or > 100
            || query.From > query.To || query.Keyword?.Length > 100 || query.Source?.Length > 64
            || query.Kind is not (null or "SystemException" or "DeviceFailure" or "ClientException")
            || query.Level is not (null or "Error" or "Warning" or "Critical"))
            return ServiceResult<PageData<ExceptionLogDto>>.Fail("VALIDATION_FAILED", "异常筛选条件不正确");
        await using var db = await factory.CreateDbContextAsync(ct);
        var records = db.ExceptionLogs.AsNoTracking();
        if (query.From is not null) records = records.Where(x => x.OccurredAt >= query.From.Value.ToUniversalTime());
        if (query.To is not null) records = records.Where(x => x.OccurredAt <= query.To.Value.ToUniversalTime());
        if (query.Kind is not null) records = records.Where(x => x.Kind == query.Kind);
        if (query.Level is not null) records = records.Where(x => x.Level == query.Level);
        if (!string.IsNullOrWhiteSpace(query.Source)) records = records.Where(x => x.Source == query.Source);
        if (query.ServerId is not null) records = records.Where(x => x.ServerId == query.ServerId);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            records = records.Where(x => x.Message.Contains(keyword) || x.ErrorCode.Contains(keyword) || x.Operation.Contains(keyword));
        }
        var total = await records.CountAsync(ct);
        // 列表不从数据库取堆栈；详情独立按主键读取，分页使用时间及 ID 的稳定顺序。
        var items = await records.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Skip((query.Current - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new ExceptionLogDto(x.Id.ToString(), x.OccurredAt, x.Kind, x.Level, x.Source, x.Operation,
                x.ErrorCode, x.Message, x.ExceptionType, null, x.TraceId, x.ServerId.HasValue ? x.ServerId.Value.ToString() : null,
                x.UserId.HasValue ? x.UserId.Value.ToString() : null, x.RequestPath, x.RequestMethod, x.HttpStatusCode)).ToArrayAsync(ct);
        return ServiceResult<PageData<ExceptionLogDto>>.Ok(new(items, total, query.Current, query.PageSize));
    }

    public async Task<ServiceResult<ExceptionLogDto>> GetAsync(long id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var item = await db.ExceptionLogs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return item is null ? ServiceResult<ExceptionLogDto>.Fail("EXCEPTION_NOT_FOUND", "异常记录不存在", 404)
            : ServiceResult<ExceptionLogDto>.Ok(ToDto(item));
    }

    private static ExceptionLogDto ToDto(SystemExceptionRecord x) => new(x.Id.ToString(), x.OccurredAt, x.Kind, x.Level,
        x.Source, x.Operation, x.ErrorCode, x.Message, x.ExceptionType, x.StackTrace, x.TraceId,
        x.ServerId?.ToString(), x.UserId?.ToString(), x.RequestPath, x.RequestMethod, x.HttpStatusCode);
}

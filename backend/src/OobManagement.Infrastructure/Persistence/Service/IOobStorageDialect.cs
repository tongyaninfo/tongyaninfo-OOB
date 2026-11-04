using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Persistence.Service;

/// <summary>数据库差异只覆盖账号锁、约束与聚合；权限、状态及设备副作用仍由原业务服务负责。</summary>
public interface IOobStorageDialect
{
    Task LockUserWritesAsync(OobDbContext db, CancellationToken ct);
    IQueryable<OobUser> ByUsername(OobDbContext db, string username);
    bool IsUniqueConflict(DbUpdateException exception, params string[] constraints);
    void ConfigureHistoryCommand(DbCommand command, long serverId, DateTimeOffset from, DateTimeOffset to, string? metricCode, int bucketSeconds);
    DateTimeOffset ReadHistoryTime(DbDataReader reader, int ordinal);
}

using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OobManagement.Desktop.Storage.Common;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Persistence.Service;

namespace OobManagement.Desktop.Storage.Service.impl;

/// <summary>只处理本地 SQLite 差异；数据库繁忙不会导致设备命令自动重发。</summary>
public sealed class SqliteStorageDialect : IOobStorageDialect
{
    public static SqliteStorageDialect Instance { get; } = new();
    public static string NormalizeUsername(string value) => value.Trim(' ').ToUpperInvariant();

    public Task LockUserWritesAsync(OobDbContext db, CancellationToken ct) =>
        // 必须在原业务事务内先获取 SQLite 写锁，再读取账号与最后管理员条件。
        db.Database.ExecuteSqlRawAsync("UPDATE \"OOB_USER\" SET \"Version\" = \"Version\" WHERE 0 = 1", ct);

    public IQueryable<OobUser> ByUsername(OobDbContext db, string username)
    {
        var normalized = NormalizeUsername(username);
        return db.Users.Where(user => user.NormalizedUsername == normalized && user.DeletedAt == null);
    }

    public bool IsUniqueConflict(DbUpdateException exception, params string[] constraints)
    {
        if (exception.InnerException is not SqliteException { SqliteExtendedErrorCode: 2067 or 1555 } sqlite) return false;
        // SQLite 提供列列表而不提供约束名称；仅识别明确白名单，不能吞掉其他唯一键错误。
        var columns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IX_OOB_USER_NormalizedUsername"] = "OOB_USER.NormalizedUsername",
            ["IX_OOB_SERVER_Address_ControllerType"] = "OOB_SERVER.Address, OOB_SERVER.ControllerType",
            ["IX_OOB_POWER_OPERATION_RequestId"] = "OOB_POWER_OPERATION.RequestId",
            ["IX_OOB_POWER_OPERATION_ServerId"] = "OOB_POWER_OPERATION.ServerId",
            ["IX_OOB_MAIL_CONFIGURATION_Slot"] = "OOB_MAIL_CONFIGURATION.Slot",
            ["IX_OOB_MAIL_MESSAGE_RequestId"] = "OOB_MAIL_MESSAGE.RequestId",
            ["PK_OOB_SYSTEM_EXCEPTION"] = "OOB_SYSTEM_EXCEPTION.Id"
        };
        return constraints.Any(constraint => columns.TryGetValue(constraint, out var expected)
            && sqlite.Message.Contains($"UNIQUE constraint failed: {expected}'", StringComparison.Ordinal));
    }

    public void ConfigureHistoryCommand(DbCommand command, long serverId, DateTimeOffset from, DateTimeOffset to, string? metricCode, int bucketSeconds)
    {
        // 负时间也按 floor 对齐 UTC epoch；所有聚合都在数据库内完成，保留空值与半开时间窗。
        command.CommandText = """
            SELECT s."SeriesId", (s."SampledAt" - ((s."SampledAt" % @bucket + @bucket) % @bucket)) AS bucket,
                   AVG(CASE WHEN s."Availability" = 'Available' THEN s."Value" END),
                   MIN(CASE WHEN s."Availability" = 'Available' THEN s."Value" END),
                   MAX(CASE WHEN s."Availability" = 'Available' THEN s."Value" END),
                   COUNT(CASE WHEN s."Availability" = 'Available' THEN s."Value" END)
            FROM "OOB_METRIC_SAMPLE" s JOIN "OOB_METRIC_SERIES" r ON r."Id" = s."SeriesId"
            WHERE r."ServerId" = @server AND s."SampledAt" >= @start AND s."SampledAt" < @end
              AND (@metric IS NULL OR r."MetricCode" = @metric)
            GROUP BY s."SeriesId", bucket ORDER BY bucket
            """;
        Parameter(command, "bucket", checked((long)bucketSeconds * 1_000_000), DbType.Int64);
        Parameter(command, "server", serverId, DbType.Int64);
        Parameter(command, "start", SqliteUtcTime.ToMicroseconds(from), DbType.Int64);
        Parameter(command, "end", SqliteUtcTime.ToMicroseconds(to), DbType.Int64);
        Parameter(command, "metric", metricCode, DbType.String);
    }

    public DateTimeOffset ReadHistoryTime(DbDataReader reader, int ordinal) => SqliteUtcTime.FromMicroseconds(reader.GetInt64(ordinal));

    private static void Parameter(DbCommand command, string name, object? value, DbType type)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value; parameter.DbType = type;
        command.Parameters.Add(parameter);
    }
}

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Persistence.Service.impl;

/// <summary>既有服务端 PostgreSQL/TimescaleDB 协议，SQL 与约束识别保持原语义。</summary>
public sealed class PostgreSqlStorageDialect : IOobStorageDialect
{
    public static PostgreSqlStorageDialect Instance { get; } = new();

    public Task LockUserWritesAsync(OobDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1869570645, 1)", ct);

    public IQueryable<OobUser> ByUsername(OobDbContext db, string username) =>
        db.Users.FromSqlInterpolated($"SELECT * FROM \"OOB_USER\" WHERE \"NormalizedUsername\" = upper(btrim({username})) AND \"DeletedAt\" IS NULL");

    public bool IsUniqueConflict(DbUpdateException exception, params string[] constraints) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
        && constraints.Contains(postgres.ConstraintName, StringComparer.Ordinal);

    public void ConfigureHistoryCommand(DbCommand command, long serverId, DateTimeOffset from, DateTimeOffset to, string? metricCode, int bucketSeconds)
    {
        command.CommandText = """
            SELECT s."SeriesId", time_bucket(make_interval(secs => @bucket), s."SampledAt", TIMESTAMPTZ '1970-01-01 00:00:00+00') AS bucket,
                   AVG(s."Value") FILTER (WHERE s."Availability" = 'Available'),
                   MIN(s."Value") FILTER (WHERE s."Availability" = 'Available'),
                   MAX(s."Value") FILTER (WHERE s."Availability" = 'Available'),
                   (COUNT(s."Value") FILTER (WHERE s."Availability" = 'Available'))::integer
            FROM "OOB_METRIC_SAMPLE" s JOIN "OOB_METRIC_SERIES" r ON r."Id"=s."SeriesId"
            WHERE r."ServerId"=@server AND s."SampledAt">=@start AND s."SampledAt"<@end
              AND (@metric IS NULL OR r."MetricCode"=@metric)
            GROUP BY s."SeriesId", bucket ORDER BY bucket
            """;
        Parameter(command, "bucket", (double)bucketSeconds);
        Parameter(command, "server", serverId);
        Parameter(command, "start", from);
        Parameter(command, "end", to);
        Parameter(command, "metric", metricCode, DbType.String);
    }

    public DateTimeOffset ReadHistoryTime(DbDataReader reader, int ordinal) =>
        new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    private static void Parameter(DbCommand command, string name, object? value, DbType? type = null)
    {
        var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value ?? DBNull.Value;
        if (type is not null) parameter.DbType = type.Value;
        command.Parameters.Add(parameter);
    }
}

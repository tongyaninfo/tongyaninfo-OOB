using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>直接核对真实 PostgreSQL/Timescale 元数据，不用内存 Provider 冒充数据库验收。</summary>
[Collection("PostgresIntegration")]
public sealed class PostgreSqlSchemaTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Exception_table_is_normal_commented_has_no_target_foreign_keys_and_uses_explicit_ids()
    {
        await using var db = await factory.DatabaseAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='OOB_SYSTEM_EXCEPTION'";
        Assert.Equal(16L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM pg_constraint WHERE conrelid='\"OOB_SYSTEM_EXCEPTION\"'::regclass AND contype='f'";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM timescaledb_information.hypertables WHERE hypertable_schema='public' AND hypertable_name='OOB_SYSTEM_EXCEPTION'";
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE schemaname='public' AND tablename='OOB_SYSTEM_EXCEPTION'";
        Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT is_identity='NO' AND column_default IS NULL FROM information_schema.columns WHERE table_schema='public' AND table_name='OOB_SYSTEM_EXCEPTION' AND column_name='Id'";
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT data_type FROM information_schema.columns WHERE table_schema='public' AND table_name='OOB_SYSTEM_EXCEPTION' AND column_name='OccurredAt'";
        Assert.Equal("timestamp with time zone", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Device_certificate_policy_has_a_true_database_default()
    {
        await using var db = await factory.DatabaseAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_default FROM information_schema.columns
            WHERE table_schema='public' AND table_name='OOB_SERVER' AND column_name='IgnoreCertificateErrors'
              AND data_type='boolean' AND is_nullable='NO'
            """;
        Assert.Equal("true", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Timescale_extension_hypertable_and_all_business_comments_exist()
    {
        await using var db = await factory.DatabaseAsync();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_extension WHERE extname='timescaledb'";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT COUNT(*) FROM timescaledb_information.hypertables WHERE hypertable_schema='public' AND hypertable_name='OOB_METRIC_SAMPLE'";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT COUNT(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relname LIKE 'OOB\_%' ESCAPE '\' AND c.relkind='r'
              AND (obj_description(c.oid,'pg_class') IS NULL OR EXISTS (
                SELECT 1 FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
                  AND col_description(c.oid,a.attnum) IS NULL))
            """;
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        command.CommandText = "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='\"OOB_METRIC_SAMPLE\"'::regclass AND contype='p'";
        var definition = (string)(await command.ExecuteScalarAsync())!;
        Assert.Contains("\"SeriesId\"", definition);
        Assert.Contains("\"SampledAt\"", definition);
        command.CommandText = "SELECT time_bucket(INTERVAL '10 seconds', TIMESTAMPTZ '2026-10-24 00:00:21+00') = TIMESTAMPTZ '2026-10-24 00:00:20+00'";
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
        command.CommandText = """
            SELECT d.description FROM pg_description d JOIN pg_class c ON c.oid=d.objoid
            JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relname LIKE 'OOB\_%' ESCAPE '\' AND c.relkind='r'
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var count = 0;
        while (await reader.ReadAsync())
        {
            Assert.Contains(reader.GetString(0), character => character is >= '\u4e00' and <= '\u9fff');
            count++;
        }
        Assert.True(count > 8, "表和字段均须交付中文说明。");
    }

    [Fact]
    public async Task Composite_sample_key_rejects_duplicate_and_keeps_different_times()
    {
        var server = await factory.CreateServerAsync();
        var serverId = long.Parse(server.Id, System.Globalization.CultureInfo.InvariantCulture);
        var seriesId = factory.Services.GetRequiredService<IIdService>().NextId();
        var time = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using (var db = await factory.DatabaseAsync())
        {
            db.Series.Add(new MetricSeries { Id = seriesId, ServerId = serverId, SensorKey = "composite-key", MetricCode = "power.watts", Name = "复合键功率", Unit = "W" });
            db.Samples.Add(new MetricSample { SeriesId = seriesId, SampledAt = time, Value = 10, Availability = "Available", Source = "Device" });
            db.Samples.Add(new MetricSample { SeriesId = seriesId, SampledAt = time.AddSeconds(1), Value = null, Availability = "TemporarilyUnavailable", Source = "Device" });
            await db.SaveChangesAsync();
        }
        await using (var duplicate = await factory.DatabaseAsync())
        {
            duplicate.Samples.Add(new MetricSample { SeriesId = seriesId, SampledAt = time, Value = 99, Availability = "Available", Source = "Device" });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }
        await using var verify = await factory.DatabaseAsync();
        Assert.Equal(2, await verify.Samples.CountAsync(sample => sample.SeriesId == seriesId));
        Assert.Equal(10, (await verify.Samples.SingleAsync(sample => sample.SeriesId == seriesId && sample.SampledAt == time)).Value);
    }
}

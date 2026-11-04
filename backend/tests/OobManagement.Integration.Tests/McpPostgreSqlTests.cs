using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>验证已迁移Testing的真实Npgsql存储和查询；不迁库、不建schema，OAuth认证另由协议测试验证。</summary>
[Collection("PostgresIntegration")]
public sealed class McpPostgreSqlTests(OobApplicationFactory application) : IClassFixture<OobApplicationFactory>
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly string[] BusinessTables = ["OOB_MCP_SETTING", "OOB_MCP_CLIENT", "OOB_MCP_CLIENT_TOOL",
        "OOB_MCP_CLIENT_SERVER", "OOB_MCP_CLIENT_NETWORK", "OOB_MCP_CALL_LOG", "OOB_MCP_CONFIG_AUDIT"];
    private static readonly string[] OAuthTables = ["OpenIddictApplications", "OpenIddictAuthorizations", "OpenIddictScopes", "OpenIddictTokens"];

    [Fact]
    public async Task Current_schema_has_seven_business_tables_four_component_tables_jsonb_comments_and_separate_histories()
    {
        await using var db = await application.DatabaseAsync();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        Assert.Contains("20261102130630_AddMcpManagement", await db.Database.GetAppliedMigrationsAsync());
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT c.relname, obj_description(c.oid,'pg_class'), a.attname, col_description(c.oid,a.attnum)
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            WHERE n.nspname=current_schema() AND c.relkind='r' AND c.relname=ANY(@names)
            ORDER BY c.relname,a.attnum
            """;
        command.Parameters.Add(new NpgsqlParameter("names", BusinessTables.Concat(OAuthTables).ToArray()));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                seen.Add(reader.GetString(0));
                Assert.False(reader.IsDBNull(1), reader.GetString(0) + "缺少表注释");
                Assert.False(reader.IsDBNull(3), reader.GetString(0) + "." + reader.GetString(2) + "缺少字段注释");
                Assert.Contains(reader.GetString(1), IsChinese);
                Assert.Contains(reader.GetString(3), IsChinese);
            }
        }
        Assert.Equal(BusinessTables.Concat(OAuthTables).Order(), seen.Order());
        command.Parameters.Clear();
        command.CommandText = """
            SELECT table_name,column_name,data_type FROM information_schema.columns
            WHERE table_schema=current_schema() AND ((table_name='OOB_MCP_SETTING' AND column_name='EnabledToolsJson')
                OR (table_name='OOB_MCP_CLIENT' AND column_name IN ('OriginsJson','RedirectUrisJson')))
            """;
        var jsonColumns = 0;
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) { Assert.Equal("jsonb", reader.GetString(2)); jsonColumns++; }
        Assert.Equal(3, jsonColumns);
        command.CommandText = "SELECT \"MigrationId\" FROM \"__McpOAuthMigrationsHistory\" ORDER BY \"MigrationId\"";
        Assert.Equal("20261102130635_AddMcpOAuthStorage", await command.ExecuteScalarAsync());
        command.CommandText = """
            SELECT count(*) FROM timescaledb_information.hypertables
            WHERE hypertable_schema=current_schema() AND (hypertable_name LIKE 'OOB_MCP_%' OR hypertable_name LIKE 'OpenIddict%')
            """;
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Npgsql_queries_filter_totals_and_aggregate_original_scoped_samples_with_zero_quality_and_separate_units()
    {
        await using var fixture = await ScopeFixture.Create(application);
        var access = await fixture.Policy.AuthorizeAsync(fixture.Request() with { GrantedServerIds = [fixture.AllowedId] }, Ct);
        Assert.True(access.Success, access.ErrorMessage);
        var list = await fixture.Query.ExecuteAsync(access.Data!, "oob_list_servers", Args(new { }), Ct);
        Assert.True(list.Success, list.ErrorMessage);
        var page = list.Data.GetProperty("data");
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(fixture.AllowedId.ToString(), Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetString());
        Assert.DoesNotContain("username", list.Data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fixture-device-secret", list.Data.GetRawText());
        var history = await fixture.Query.ExecuteAsync(access.Data!, "oob_query_history", Args(new
        {
            serverId = fixture.AllowedId.ToString(), from = fixture.From.ToOffset(TimeSpan.FromHours(8)),
            to = fixture.From.AddSeconds(50), metricCode = "temperature.celsius", bucketSeconds = 10, maxPoints = 20
        }), Ct);
        Assert.True(history.Success, history.ErrorMessage);
        var series = Assert.Single(history.Data.GetProperty("data").GetProperty("series").EnumerateArray());
        Assert.Equal("°C", series.GetProperty("unit").GetString());
        Assert.Equal("Redfish", Assert.Single(series.GetProperty("sources").EnumerateArray()).GetString());
        var points = series.GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(5, points.Length);
        Assert.Equal(20, points[0].GetProperty("average").GetDouble());
        Assert.Equal(2, points[0].GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, points[1].GetProperty("average").ValueKind);
        Assert.Equal(0, points[1].GetProperty("count").GetInt32());
        Assert.Equal(0, points[2].GetProperty("average").GetDouble());
        var overview = await fixture.Query.ExecuteAsync(access.Data!, "oob_get_overview", Args(new { from = fixture.From, to = fixture.From.AddMinutes(1) }), Ct);
        Assert.True(overview.Success, overview.ErrorMessage);
        Assert.Equal(1, overview.Data.GetProperty("data").GetProperty("totalServers").GetInt32());
        Assert.DoesNotContain(fixture.HiddenId.ToString(), overview.Data.GetRawText());
        var report = await fixture.Query.ExecuteAsync(access.Data!, "oob_preview_operation_report", Args(new
        { from = fixture.From, to = fixture.From.AddDays(1), period = "day" }), Ct);
        Assert.True(report.Success, report.ErrorMessage);
        var data = report.Data.GetProperty("data");
        var poll = Assert.Single(data.GetProperty("servers").EnumerateArray());
        Assert.Equal(2, poll.GetProperty("total").GetInt32());
        Assert.Equal(1, poll.GetProperty("successful").GetInt32());
        Assert.Equal(1, poll.GetProperty("failed").GetInt32());
        var sensors = data.GetProperty("sensors").EnumerateArray().ToArray();
        Assert.Equal(4, sensors.Length);
        var temperature = Assert.Single(sensors, x => x.GetProperty("metricCode").GetString() == "temperature.celsius");
        Assert.Equal("3", temperature.GetProperty("count").GetString());
        Assert.Equal(40d / 3, temperature.GetProperty("average").GetDouble(), precision: 10);
        var power = Assert.Single(sensors, x => x.GetProperty("metricCode").GetString() == "power.watts");
        Assert.Equal(0, power.GetProperty("average").GetDouble());
        var rpm = Assert.Single(sensors, x => x.GetProperty("metricCode").GetString() == "fan.rpm");
        Assert.Equal("RPM", rpm.GetProperty("unit").GetString()); Assert.Equal(1000, rpm.GetProperty("average").GetDouble());
        var percent = Assert.Single(sensors, x => x.GetProperty("metricCode").GetString() == "fan.percent");
        Assert.Equal("%", percent.GetProperty("unit").GetString()); Assert.Equal(50, percent.GetProperty("average").GetDouble());
        Assert.DoesNotContain("99999", report.Data.GetRawText());
        Assert.DoesNotContain(fixture.HiddenId.ToString(), report.Data.GetRawText());
        Assert.Equal(0, application.Device.ReadCount);
        Assert.Equal(0, application.Device.WriteCount);
        Assert.Equal(0, application.DellDevice.ReadCount);
        Assert.Equal(0, application.DellDevice.WriteCount);
    }

    [Fact]
    public async Task Current_policy_and_signed_consent_intersect_and_scope_or_identity_revocation_is_immediate_on_Npgsql()
    {
        await using var fixture = await ScopeFixture.Create(application);
        var request = fixture.Request() with { GrantedTools = ["oob_list_servers", "oob_get_server"], GrantedServerIds = [fixture.AllowedId] };
        var access = await fixture.Policy.AuthorizeAsync(request, Ct);
        Assert.True(access.Success, access.ErrorMessage);
        Assert.False(access.Data!.AllServers);
        Assert.Equal([fixture.AllowedId], access.Data.ServerIds);
        Assert.DoesNotContain("oob_preview_operation_report", access.Data.Tools);
        Assert.Equal("MCP_RESOURCE_INACCESSIBLE", (await fixture.Policy.AuthorizeAsync(request with
        { Tool = "oob_get_server", ServerId = fixture.HiddenId }, Ct)).ErrorCode);
        Assert.Equal("MCP_TOOL_DENIED", (await fixture.Policy.AuthorizeAsync(request with { Tool = "oob_preview_operation_report" }, Ct)).ErrorCode);
        Assert.Equal("MCP_NETWORK_DENIED", (await fixture.Policy.AuthorizeAsync(request with { SourceIp = "203.0.113.1" }, Ct)).ErrorCode);
        Assert.Equal("MCP_ORIGIN_DENIED", (await fixture.Policy.AuthorizeAsync(request with { Origin = "https://foreign.invalid" }, Ct)).ErrorCode);
        var widened = await fixture.Management.SaveClientAsync(fixture.Actor, fixture.ClientId,
            fixture.ClientRequest() with { ExpectedVersion = fixture.Client.Version, ServerScope = "AllServers" }, Ct);
        Assert.True(widened.Success, widened.ErrorMessage);
        fixture.Client = widened.Data!;
        var oldConsent = await fixture.Policy.AuthorizeAsync(request, Ct);
        Assert.True(oldConsent.Success, oldConsent.ErrorMessage);
        Assert.False(oldConsent.Data!.AllServers);
        Assert.Equal([fixture.AllowedId], oldConsent.Data.ServerIds);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var user = await db.Users.SingleAsync(x => x.Id == fixture.Actor.UserId);
            user.AuthVersion++; await db.SaveChangesAsync();
        }
        Assert.Equal("MCP_IDENTITY_REVOKED", (await fixture.Policy.AuthorizeAsync(request, Ct)).ErrorCode);
    }

    [Fact]
    public async Task Npgsql_persists_jsonb_client_rules_call_audit_and_configuration_audit_without_secret_material()
    {
        await using var fixture = await ScopeFixture.Create(application);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            Assert.Equal(["https://mcp-fixture.invalid"], McpRules.Read((await db.McpClients.SingleAsync(x => x.Id == fixture.ClientId)).OriginsJson));
            Assert.Equal(2, await db.McpClientServers.CountAsync(x => x.ClientId == fixture.ClientId));
            Assert.Equal("192.0.2.0/24", (await db.McpClientNetworks.SingleAsync(x => x.ClientId == fixture.ClientId)).Network);
        }
        var audit = new McpAuditService(fixture.Factory, fixture.Ids, new McpAuditOptions(), TimeProvider.System);
        var start = await audit.StartAsync(new("tools/call", "oob_get_server", fixture.AllowedId.ToString(), "192.0.2.20",
            "2026-07-28", "pg-fixture-" + fixture.Actor.UserId, "protocol-sensitive-marker", fixture.Client.Id, fixture.Client.Name,
            fixture.Actor.UserId.ToString(), "policy:" + fixture.Client.Version,
            Args(new { serverId = fixture.AllowedId.ToString(), password = "must-not-be-saved" })), Ct);
        Assert.True(start.Success, start.ErrorMessage);
        await audit.CompleteAsync(start.Data!, new("Succeeded", OutputSummary: "must-not-be-saved"), Ct);
        var logs = await fixture.Management.ListLogsAsync(fixture.Actor, new() { ClientId = fixture.Client.Id }, Ct);
        Assert.True(logs.Success, logs.ErrorMessage);
        var row = Assert.Single(logs.Data!.Items);
        var detail = await fixture.Management.GetLogAsync(fixture.Actor, long.Parse(row.Id), Ct);
        Assert.True(detail.Success);
        Assert.Equal("Succeeded", detail.Data!.Result);
        Assert.Equal(JsonValueKind.String, JsonSerializer.SerializeToElement(detail.Data, JsonStorage.Options).GetProperty("id").ValueKind);
        Assert.DoesNotContain("must-not-be-saved", JsonSerializer.Serialize(detail.Data));
        Assert.DoesNotContain("protocol-sensitive-marker", JsonSerializer.Serialize(detail.Data));
        Assert.Contains((await fixture.Management.ListConfigurationAuditsAsync(fixture.Actor, 1, 100, Ct)).Data!.Items, x => x.ObjectId == fixture.Client.Id);
    }

    private static bool IsChinese(char value) => value is >= '\u4e00' and <= '\u9fff';
    private static JsonElement Args<T>(T value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class ScopeFixture : IAsyncDisposable
    {
        private readonly OobApplicationFactory _application;
        public IDbContextFactory<OobDbContext> Factory { get; }
        public IIdService Ids { get; }
        public UserSessionContext Actor { get; private set; }
        public long AllowedId { get; private set; }
        public long HiddenId { get; private set; }
        public long ClientId => long.Parse(Client.Id);
        public McpClientDto Client { get; set; } = null!;
        public McpManagementService Management { get; }
        public McpPolicyService Policy { get; }
        public McpQueryService Query { get; }
        public DateTimeOffset From { get; } = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        private long _settingId;

        private ScopeFixture(OobApplicationFactory application)
        {
            _application = application;
            Factory = application.Services.GetRequiredService<IDbContextFactory<OobDbContext>>();
            Ids = application.Services.GetRequiredService<IIdService>();
            var authentication = application.Services.GetRequiredService<IAuthenticationService>();
            var ready = new Ready();
            // 此处仅替换宿主就绪与注册适配；策略、EF查询和审计继续使用真实Npgsql。
            Management = new(Factory, Ids, authentication, new Registration(), ready, TimeProvider.System);
            Policy = new(Factory, authentication, ready, TimeProvider.System);
            Query = new(Factory, Policy, application.Services.GetRequiredService<IServerManagementService>(), TimeProvider.System);
        }

        public static async Task<ScopeFixture> Create(OobApplicationFactory application)
        {
            var fixture = new ScopeFixture(application);
            try
            {
                var actor = await application.SeedUserAsync(administrator: true, canOperatePower: true);
                fixture.Actor = new(actor.Id, actor.AuthVersion);
                fixture.AllowedId = long.Parse((await application.CreateServerAsync()).Id);
                fixture.HiddenId = long.Parse((await application.CreateServerAsync()).Id);
                await using (var db = await fixture.Factory.CreateDbContextAsync())
                {
                    // 单例只能由本次创建；已有配置不覆盖、不借用，也不为了测试更改既有MCP开关。
                    Assert.False(await db.McpSettings.AnyAsync(), "Testing 已有MCP配置，不能覆盖，请为此用例提供空的测试单例槽位");
                    fixture._settingId = fixture.Ids.NextId();
                    db.McpSettings.Add(new() { Id = fixture._settingId, Enabled = true, Version = 1,
                        EnabledToolsJson = McpRules.Write(McpToolCatalog.All.Select(t => t.Name)), UpdatedByUserId = fixture.Actor.UserId, UpdatedAt = fixture.From });
                    foreach (var id in new[] { fixture.AllowedId, fixture.HiddenId })
                    {
                        var server=await db.Servers.SingleAsync(x=>x.Id==id);
                        server.CreatedAt=fixture.From.AddDays(-1);
                        var temperature = fixture.Sensor(id, "cpu", "temperature.celsius", "°C");
                        db.Series.Add(temperature);
                        db.Samples.AddRange(fixture.Sample(temperature.Id, 1, id == fixture.AllowedId ? 10 : 99999),
                            fixture.Sample(temperature.Id, 2, id == fixture.AllowedId ? 30 : 99999),
                            fixture.Sample(temperature.Id, 3, 9999, "Unsupported"), fixture.Sample(temperature.Id, 21, 0),
                            fixture.Sample(temperature.Id, 31, null, "TemporarilyUnavailable"));
                        if (id == fixture.AllowedId)
                        {
                            foreach (var (code, unit, value) in new[] { ("power.watts", "W", 0d), ("fan.rpm", "RPM", 1000d), ("fan.percent", "%", 50d) })
                            {
                                var sensor = fixture.Sensor(id, code, code, unit); db.Series.Add(sensor);
                                db.Samples.Add(fixture.Sample(sensor.Id, 1, value));
                                if (code == "fan.percent") db.Samples.Add(fixture.Sample(sensor.Id, 2, 200));
                            }
                        }
                        db.PollRuns.AddRange(new PollRun { Id = fixture.Ids.NextId(), ServerId = id, StartedAt = fixture.From,
                                CompletedAt = fixture.From.AddSeconds(2), Status = "Success" },
                            new PollRun { Id = fixture.Ids.NextId(), ServerId = id, StartedAt = fixture.From.AddSeconds(10),
                                CompletedAt = fixture.From.AddSeconds(14), Status = "Failed" });
                    }
                    await db.SaveChangesAsync();
                }
                var created = await fixture.Management.SaveClientAsync(fixture.Actor, null, fixture.ClientRequest(), Ct);
                Assert.True(created.Success, created.ErrorMessage);
                fixture.Client = created.Data!;
                var enabled = await fixture.Management.SetClientEnabledAsync(fixture.Actor, fixture.ClientId, true, new(fixture.Client.Version), Ct);
                Assert.True(enabled.Success, enabled.ErrorMessage); fixture.Client = enabled.Data!;
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public McpClientRequest ClientRequest() => new()
        {
            Name = _application.RunPrefix + "-mcp-client", UserId = Actor.UserId.ToString(),
            Tools = McpToolCatalog.All.Select(x => x.Name).ToArray(), ServerIds = [AllowedId.ToString(), HiddenId.ToString()],
            Networks = ["192.0.2.129/24"], Origins = ["https://mcp-fixture.invalid"], RedirectUris = ["https://mcp-fixture.invalid/callback"]
        };
        public McpAccessRequest Request() => new(Client.OAuthClientId, Actor.UserId, Actor.AuthVersion,
            McpToolCatalog.All.Select(x => x.Name).ToArray(), false, [AllowedId, HiddenId], "192.0.2.20", null);
        private MetricSeries Sensor(long server, string key, string code, string unit) => new()
        { Id = Ids.NextId(), ServerId = server, SensorKey = key, Name = _application.RunPrefix + "-" + key, MetricCode = code, Unit = unit };
        private MetricSample Sample(long series, int second, double? value, string availability = "Available") => new()
        { SeriesId = series, SampledAt = From.AddSeconds(second), Value = value, Availability = availability, Source = "Redfish" };

        public async ValueTask DisposeAsync()
        {
            if (Actor.UserId == 0 && _settingId == 0) return;
            await using var db = await Factory.CreateDbContextAsync();
            var ownClients = db.McpClients.Where(x => x.UserId == Actor.UserId && x.Name == _application.RunPrefix + "-mcp-client").Select(x => x.Id);
            // 仅清理本次用户和客户端ID所属DML；业务服务器由原夹具按唯一前缀清理，不删除表、schema或文件。
            await db.McpClientTools.Where(x => ownClients.Contains(x.ClientId)).ExecuteDeleteAsync();
            await db.McpClientServers.Where(x => ownClients.Contains(x.ClientId)).ExecuteDeleteAsync();
            await db.McpClientNetworks.Where(x => ownClients.Contains(x.ClientId)).ExecuteDeleteAsync();
            await db.McpCallLogs.Where(x => x.UserId == Actor.UserId).ExecuteDeleteAsync();
            await db.McpConfigAudits.Where(x => x.UserId == Actor.UserId).ExecuteDeleteAsync();
            await db.McpClients.Where(x => ownClients.Contains(x.Id)).ExecuteDeleteAsync();
            if (_settingId != 0) await db.McpSettings.Where(x => x.Id == _settingId).ExecuteDeleteAsync();
        }
    }

    private sealed class Ready : IMcpReadinessService
    {
        public Task<McpHostStatus> ReadAsync(CancellationToken ct) => Task.FromResult(new McpHostStatus(true, true,
            "https://mcp-postgresql-fixture.invalid/mcp", ["2026-07-28", "2025-11-25"], []));
    }
    private sealed class Registration : IMcpOAuthRegistrationService
    {
        public Task<ServiceResult<bool>> SyncAsync(McpOAuthClientRegistration request, CancellationToken ct) => Task.FromResult(ServiceResult<bool>.Ok(true));
        public Task<ServiceResult<bool>> RevokeAsync(string clientId, CancellationToken ct) => Task.FromResult(ServiceResult<bool>.Ok(true));
        public Task<ServiceResult<McpSecretDto>> RotateSecretAsync(string clientId, CancellationToken ct) => throw new InvalidOperationException("该测试不发放或轮换OAuth秘密");
    }
}

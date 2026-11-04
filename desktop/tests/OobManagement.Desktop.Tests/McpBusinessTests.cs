using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>每例独立创建仓库外新文件SQLite并保留；不访问既有库、OAuth服务、真机或SMTP。</summary>
public sealed class McpBusinessTests
{
    private static readonly CancellationToken Ct=CancellationToken.None;
    [Fact]
    public async Task Configuration_reads_do_not_create_settings_and_explicit_enable_requires_complete_whitelists()
    {
        var f=await Fixture.Create();
        var initial=await f.Management.ConfigurationAsync(f.Actor,Ct);
        Assert.True(initial.Success);Assert.False(initial.Data!.Enabled);Assert.Equal("0",initial.Data.Version);
        await using(var db=await f.Factory.CreateDbContextAsync())Assert.Empty(await db.McpSettings.ToArrayAsync());
        var saved=await f.Management.SaveConfigurationAsync(f.Actor,new(){EnabledTools=McpToolCatalog.All.Select(x=>x.Name).ToArray()},Ct);
        Assert.True(saved.Success);Assert.False(saved.Data!.Enabled);
        var denied=await f.Management.SetEnabledAsync(f.Actor,true,new(saved.Data.Version),Ct);
        Assert.False(denied.Success);
        var client=await f.Register();Assert.False(client.Enabled);Assert.Equal("public",client.ClientType);
        var enabled=await f.Management.SetClientEnabledAsync(f.Actor,long.Parse(client.Id),true,new(client.Version),Ct);
        Assert.True(enabled.Success,enabled.ErrorMessage);
        var activated=await f.Management.SetEnabledAsync(f.Actor,true,new(saved.Data.Version),Ct);
        Assert.True(activated.Success,activated.ErrorMessage);Assert.True(activated.Data!.Enabled);
        var conflict=await f.Management.SaveConfigurationAsync(f.Actor,new(){ExpectedVersion=saved.Data.Version},Ct);
        Assert.Equal("MCP_VERSION_CONFLICT",conflict.ErrorCode);
        Assert.Equal(409,conflict.StatusCode);
    }
    [Fact]
    public async Task Consent_snapshot_cannot_gain_new_devices_or_tools_and_network_origin_identity_are_revalidated()
    {
        var f=await Fixture.Create();var client=await f.Activate();
        var request=f.Request(client) with { GrantedTools=["oob_list_servers","oob_get_server"],GrantedServerIds=[f.First],ServerId=f.Second,Tool="oob_get_server" };
        Assert.Equal("MCP_RESOURCE_INACCESSIBLE",(await f.Policy.AuthorizeAsync(request,Ct)).ErrorCode);
        var allowed=await f.Policy.AuthorizeAsync(request with{ServerId=f.First},Ct);
        Assert.True(allowed.Success);Assert.Equal([f.First],allowed.Data!.ServerIds);Assert.DoesNotContain("oob_query_history",allowed.Data.Tools);
        Assert.Equal("MCP_NETWORK_DENIED",(await f.Policy.AuthorizeAsync(request with{SourceIp="203.0.113.1"},Ct)).ErrorCode);
        Assert.Equal("MCP_ORIGIN_DENIED",(await f.Policy.AuthorizeAsync(request with{Origin="https://wrong.example"},Ct)).ErrorCode);
        Assert.True((await f.Policy.AuthorizeAsync(request with{ServerId=f.First,SourceIp="::ffff:192.0.2.20"},Ct)).Success);
        await using(var db=await f.Factory.CreateDbContextAsync()) {var user=await db.Users.SingleAsync();user.AuthVersion++;await db.SaveChangesAsync();}
        Assert.Equal("MCP_IDENTITY_REVOKED",(await f.Policy.AuthorizeAsync(request with{ServerId=f.First},Ct)).ErrorCode);
    }
    [Fact]
    public async Task Scoped_lists_overview_history_and_report_do_not_leak_totals_values_or_device_credentials()
    {
        var f=await Fixture.Create();var client=await f.Activate();
        var request=f.Request(client) with { GrantedServerIds=[f.First] };
        var authorized=await f.Policy.AuthorizeAsync(request,Ct);Assert.True(authorized.Success);
        var list=await f.Query.ExecuteAsync(authorized.Data!,"oob_list_servers",Args(new{}),Ct);
        Assert.True(list.Success,list.ErrorMessage);Assert.Equal(1,list.Data.GetProperty("data").GetProperty("total").GetInt32());
        Assert.DoesNotContain("fixture-device-secret",list.Data.GetRawText());Assert.DoesNotContain("private-bmc",list.Data.GetRawText());
        Assert.DoesNotContain("username",list.Data.GetRawText(),StringComparison.OrdinalIgnoreCase);
        var overview=await f.Query.ExecuteAsync(authorized.Data!,"oob_get_overview",Args(new{from=f.From,to=f.From.AddMinutes(10)}),Ct);
        Assert.True(overview.Success,overview.ErrorMessage);Assert.Equal(1,overview.Data.GetProperty("data").GetProperty("totalServers").GetInt32());
        var history=await f.Query.ExecuteAsync(authorized.Data!,"oob_query_history",Args(new{serverId=f.First.ToString(),from=f.From,to=f.From.AddMinutes(10),bucketSeconds=60,maxPoints=20}),Ct);
        Assert.True(history.Success,history.ErrorMessage);Assert.DoesNotContain("99999",history.Data.GetRawText());
        var report=await f.Query.ExecuteAsync(authorized.Data!,"oob_preview_operation_report",Args(new{from=f.From,to=f.From.AddDays(1),period="day"}),Ct);
        Assert.True(report.Success,report.ErrorMessage);
        var data=report.Data.GetProperty("data");Assert.Single(data.GetProperty("servers").EnumerateArray());
        var sensor=Assert.Single(data.GetProperty("sensors").EnumerateArray());Assert.Equal(20,sensor.GetProperty("average").GetDouble());
        Assert.Equal("2",sensor.GetProperty("count").GetString());Assert.Equal("Redfish",sensor.GetProperty("source").GetString());
        var outOfRange=await f.Query.ExecuteAsync(authorized.Data!,"oob_get_server",Args(new{serverId=f.Second.ToString()}),Ct);
        Assert.Equal("MCP_RESOURCE_INACCESSIBLE",outOfRange.ErrorCode);
        Assert.Equal("MCP_TOOL_DENIED",(await f.Query.ExecuteAsync(authorized.Data!,"oob_list_exceptions",Args(new{}),Ct)).ErrorCode);
    }
    [Fact]
    public async Task Concurrent_expected_versions_cannot_overwrite_and_component_failure_leaves_client_disabled()
    {
        var f=await Fixture.Create();var client=await f.Activate();
        var id=long.Parse(client.Id);
        var writes=await Task.WhenAll(f.Management.SetClientEnabledAsync(f.Actor,id,false,new(client.Version),Ct),f.Management.SetClientEnabledAsync(f.Actor,id,false,new(client.Version),Ct));
        Assert.Single(writes,x=>x.Success);Assert.Equal("MCP_VERSION_CONFLICT",Assert.Single(writes,x=>!x.Success).ErrorCode);
        var current=(await f.Management.GetClientAsync(f.Actor,id,Ct)).Data!;
        current=(await f.Management.SetClientEnabledAsync(f.Actor,id,true,new(current.Version),Ct)).Data!;
        f.OAuth.Fail=true;
        var edit=await f.Management.SaveClientAsync(f.Actor,id,f.ClientRequest() with{ExpectedVersion=current.Version,Name="changed"},Ct);
        Assert.False(edit.Success);Assert.False((await f.Management.GetClientAsync(f.Actor,id,Ct)).Data!.Enabled);
        await using var db=await f.Factory.CreateDbContextAsync();Assert.NotEmpty(await db.McpConfigAudits.ToArrayAsync());
    }
    [Fact]
    public async Task Audit_falls_back_to_durable_redacted_append_journal_replays_without_duplicates_and_never_deletes_files()
    {
        var f=await Fixture.Create();var directory=Path.Combine(f.Directory,"audit");var options=new McpAuditOptions{SpoolDirectory=directory};
        var failed=new McpAuditService(new BrokenFactory(),f.Ids,options,TimeProvider.System);
        var started=await failed.StartAsync(new("tools/call","oob_query_history",f.First.ToString(),"192.0.2.20","2026-07-28","trace","protocol-private-secret",
            Arguments:Args(new{serverId=f.First.ToString(),keyword="query-secret",password="device-secret"})),Ct);
        Assert.True(started.Success,started.ErrorMessage);
        await failed.CompleteAsync(started.Data!,new("Succeeded",OutputSummary:"output-private-secret"),Ct);
        var path=Path.Combine(directory,"mcp-audit.jsonl");var content=await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("query-secret",content);Assert.DoesNotContain("device-secret",content);Assert.DoesNotContain("output-private-secret",content);Assert.DoesNotContain("protocol-private-secret",content);
        var recovered=new McpAuditService(f.Factory,f.Ids,options,TimeProvider.System);
        await recovered.ReplayAsync(Ct);await recovered.ReplayAsync(Ct);
        await using var db=await f.Factory.CreateDbContextAsync();var row=Assert.Single(await db.McpCallLogs.ToArrayAsync());
        Assert.Equal("Succeeded",row.Result);Assert.NotNull(row.CompletedAt);Assert.True(File.Exists(path));
        var unavailable=new McpAuditService(new BrokenFactory(),f.Ids,new McpAuditOptions(),TimeProvider.System);
        Assert.Equal("MCP_AUDIT_UNAVAILABLE",(await unavailable.StartAsync(new("tools/list",null,null,"192.0.2.20",null,null,null),Ct)).ErrorCode);
    }
    [Fact]
    public async Task Schema_unknown_parameters_invalid_ranges_and_required_fields_fail_before_saved_data_queries()
    {
        var f=await Fixture.Create();var client=await f.Activate();var context=(await f.Policy.AuthorizeAsync(f.Request(client),Ct)).Data!;
        foreach(var args in new[]{Args(new{serverId=f.First.ToString(),password="bad"}),Args(new{serverId=f.First.ToString(),maxPoints="bad"}),Args(new{maxPoints=0}),Args(new{})})
            Assert.Equal("MCP_INVALID_ARGUMENTS",(await f.Query.ExecuteAsync(context,"oob_query_history",args,Ct)).ErrorCode);
        Assert.All(McpToolCatalog.All,t=>Assert.True(t.OutputSchema.GetProperty("properties").TryGetProperty("schemaVersion",out _)));
        Assert.Equal(10,McpToolCatalog.All.Select(x=>x.Name).Distinct().Count());
    }
    [Fact]
    public async Task All_ten_tools_use_saved_data_and_project_safe_inventory_telemetry_exceptions_and_power_history()
    {
        var f=await Fixture.Create();var client=await f.Activate();
        var changed=await f.Management.SaveClientAsync(f.Actor,long.Parse(client.Id),f.ClientRequest() with{ExpectedVersion=client.Version,ServerScope="AllServers"},Ct);
        Assert.True(changed.Success,changed.ErrorMessage);
        var faultId=f.Ids.NextId();
        await using(var db=await f.Factory.CreateDbContextAsync())
        {
            db.Inventories.Add(new(){Id=f.Ids.NextId(),ServerId=f.First,CollectedAt=f.From,PayloadJson=JsonStorage.Write(new DeviceInventory{CollectedAt=f.From,
                Components=[new(){Key="disk",Kind="Disk",Name="Disk",CapacityBytes=9_100_000_000_000_000,Attributes=new(){["Password"]="inventory-secret"}}]})});
            var server=await db.Servers.SingleAsync(x=>x.Id==f.First);server.LatestTelemetryJson=JsonStorage.Write(new TelemetryReading{Metrics=[new(){SensorKey="cpu",Name="CPU",MetricCode="temperature.celsius",Value=42,Unit="°C",Source="Redfish"}]});
            server.LastCollectedAt=f.From;
            db.ExceptionLogs.Add(new(){Id=faultId,OccurredAt=f.From,Kind="DeviceFailure",Source="Collector",Message="安全摘要",StackTrace="private-stack-secret",ServerId=f.First});
            db.PowerOperations.Add(new(){Id=f.Ids.NextId(),ServerId=f.First,UserId=f.Actor.UserId,RequestId=Guid.NewGuid().ToString(),Action="On",Status="Unknown",CreatedAt=f.From});
            await db.SaveChangesAsync();
        }
        var auth=await f.Policy.AuthorizeAsync(f.Request(changed.Data!) with{GrantedAllServers=true},Ct);Assert.True(auth.Success);
        var window=new{from=f.From,to=f.From.AddDays(1)};
        var calls=new Dictionary<string,JsonElement>
        {
            ["oob_list_servers"]=Args(new{}),["oob_get_server"]=Args(new{serverId=f.First.ToString()}),
            ["oob_get_inventory"]=Args(new{serverId=f.First.ToString()}),["oob_get_latest_telemetry"]=Args(new{serverId=f.First.ToString()}),
            ["oob_query_history"]=Args(new{serverId=f.First.ToString(),window.from,window.to}),["oob_get_overview"]=Args(window),
            ["oob_list_exceptions"]=Args(window),["oob_get_exception"]=Args(new{exceptionId=faultId.ToString()}),
            ["oob_list_power_operations"]=Args(new{serverId=f.First.ToString(),window.from,window.to}),["oob_preview_operation_report"]=Args(window)
        };
        foreach(var call in calls)
        {
            var result=await f.Query.ExecuteAsync(auth.Data!,call.Key,call.Value,Ct);Assert.True(result.Success,call.Key+":"+result.ErrorMessage);
            Assert.DoesNotContain("inventory-secret",result.Data.GetRawText());Assert.DoesNotContain("private-stack-secret",result.Data.GetRawText());
            if(call.Key=="oob_get_inventory")Assert.Equal("9100000000000000",result.Data.GetProperty("data").GetProperty("components")[0].GetProperty("capacityBytes").GetString());
            if(call.Key=="oob_get_latest_telemetry")Assert.Equal("Redfish",result.Data.GetProperty("data").GetProperty("metrics")[0].GetProperty("source").GetString());
        }
    }
    [Fact]
    public async Task Audit_preserves_half_written_journal_and_recovers_later_complete_records()
    {
        var f=await Fixture.Create();var directory=Path.Combine(f.Directory,"half-journal");System.IO.Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,"mcp-audit.jsonl");await File.WriteAllTextAsync(path,"{\"incomplete\":");
        var options=new McpAuditOptions{SpoolDirectory=directory};var down=new McpAuditService(new BrokenFactory(),f.Ids,options,TimeProvider.System);
        var started=await down.StartAsync(new("oauth/authorize",null,null,"192.0.2.20",null,null,null),Ct);Assert.True(started.Success);
        await down.CompleteAsync(started.Data!,new("Succeeded"),Ct);
        var up=new McpAuditService(f.Factory,f.Ids,options,TimeProvider.System);await up.ReplayAsync(Ct);
        await using var db=await f.Factory.CreateDbContextAsync();var row=Assert.Single(await db.McpCallLogs.ToArrayAsync());
        Assert.Equal("oauth/authorize",row.Method);Assert.Equal("Succeeded",row.Result);
        Assert.StartsWith("{\"incomplete\":",await File.ReadAllTextAsync(path));
    }
    [Theory]
    [InlineData("192.0.2.129/25","192.0.2.128/25","192.0.2.255",true)]
    [InlineData("192.0.2.129/25","192.0.2.128/25","192.0.2.127",false)]
    [InlineData("2001:db8::1234/64","2001:db8::/64","2001:db8::ffff",true)]
    [InlineData("2001:db8::1234/64","2001:db8::/64","2001:db9::1",false)]
    [InlineData("::ffff:192.0.2.20/120","192.0.2.0/24","::ffff:192.0.2.30",true)]
    public void Network_rules_normalize_boundaries_and_mapped_IPs(string input,string normalized,string address,bool allowed)
    {
        Assert.True(McpRules.TryNetwork(input,out var actual));Assert.Equal(normalized,actual);Assert.Equal(allowed,McpRules.InNetwork(address,normalized));
        Assert.False(McpRules.TryNetwork("192.0.2.1/33",out _));Assert.False(McpRules.Origin("https://client.example/path"));
        Assert.False(McpRules.Redirect("https://client.example/callback#fragment"));Assert.True(McpRules.Redirect("http://127.0.0.1:54321/callback"));
    }
    private static JsonElement Args<T>(T value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private sealed class Fixture
    {
        public string Directory {get;}=Path.Combine(Path.GetTempPath(),"oob-mcp-business-tests",Guid.NewGuid().ToString("N"));
        public Ids Ids {get;}=new();public SqliteOobDbContextFactory Factory {get;private set;}=null!;
        public McpManagementService Management {get;private set;}=null!;public McpPolicyService Policy {get;private set;}=null!;
        public McpQueryService Query {get;private set;}=null!;public OAuth OAuth {get;}=new();
        public UserSessionContext Actor {get;private set;} public long First {get;private set;}public long Second {get;private set;}
        public DateTimeOffset From {get;}=new(2026,11,1,0,0,0,TimeSpan.Zero);
        public static async Task<Fixture> Create()
        {
            var f=new Fixture();System.IO.Directory.CreateDirectory(f.Directory);var path=Path.Combine(f.Directory,"mcp.sqlite");
            await using(var db=SqliteOobDbContextFactory.Create(path,allowCreate:true))await db.Database.EnsureCreatedAsync();
            f.Factory=new(path);var config=new ConfigurationBuilder().Build();var auth=new AuthenticationService(f.Factory,f.Ids,config,new Revoker());
            f.Policy=new(f.Factory,auth,new Readiness(),TimeProvider.System);f.Management=new(f.Factory,f.Ids,auth,f.OAuth,new Readiness(),TimeProvider.System);
            var servers=new ServerManagementService(f.Factory,f.Ids,new EphemeralDataProtectionProvider(),[],config,
                NullLogger<ServerManagementService>.Instance,new Publisher(),new Recorder(),auth);
            f.Query=new(f.Factory,f.Policy,servers,TimeProvider.System);f.Actor=new(f.Ids.NextId(),1);f.First=f.Ids.NextId();f.Second=f.Ids.NextId();
            await using var seed=await f.Factory.CreateDbContextAsync();
            seed.Users.Add(new(){Id=f.Actor.UserId,Username="mcp-admin",DisplayName="mcp-admin",Enabled=true,IsAdministrator=true,CanOperatePower=true,CreatedAt=f.From,UpdatedAt=f.From});
            foreach(var id in new[]{f.First,f.Second})
            {
                seed.Servers.Add(new(){Id=id,Name=id==f.First?"allowed":"hidden",Address="https://private-bmc-"+id+".invalid/",Username="private-account",PasswordCiphertext="fixture-device-secret",Brand="HP",ControllerType="iLO4",CreatedAt=f.From.AddDays(-1),UpdatedAt=f.From});
                var series=f.Ids.NextId();seed.Series.Add(new(){Id=series,ServerId=id,SensorKey="cpu",Name="CPU",MetricCode="temperature.celsius",Unit="°C"});
                seed.Samples.AddRange(new MetricSample{SeriesId=series,SampledAt=f.From,Value=id==f.First?10:99999,Availability="Available",Source="Redfish"},
                    new MetricSample{SeriesId=series,SampledAt=f.From.AddSeconds(10),Value=id==f.First?30:99999,Availability="Available",Source="Redfish"},
                    new MetricSample{SeriesId=series,SampledAt=f.From.AddSeconds(20),Value=9999,Availability="Unsupported",Source="Redfish"});
                seed.PollRuns.Add(new(){Id=f.Ids.NextId(),ServerId=id,StartedAt=f.From,CompletedAt=f.From.AddSeconds(1),Status="Success"});
            }
            await seed.SaveChangesAsync();return f;
        }
        public McpClientRequest ClientRequest()=>new(){Name="fixture-client",UserId=Actor.UserId.ToString(),Tools=McpToolCatalog.All.Select(x=>x.Name).ToArray(),
            ServerIds=[First.ToString(),Second.ToString()],Networks=["192.0.2.0/24"],Origins=["https://client.example"],RedirectUris=["https://client.example/callback"]};
        public async Task<McpClientDto> Register(){var r=await Management.SaveClientAsync(Actor,null,ClientRequest(),Ct);Assert.True(r.Success,r.ErrorMessage);return r.Data!;}
        public async Task<McpClientDto> Activate()
        {
            var setting=await Management.SaveConfigurationAsync(Actor,new(){EnabledTools=McpToolCatalog.All.Select(x=>x.Name).ToArray()},Ct);Assert.True(setting.Success,setting.ErrorMessage);
            var c=await Register();var enabled=await Management.SetClientEnabledAsync(Actor,long.Parse(c.Id),true,new(c.Version),Ct);Assert.True(enabled.Success,enabled.ErrorMessage);
            var activated=await Management.SetEnabledAsync(Actor,true,new(setting.Data!.Version),Ct);Assert.True(activated.Success,activated.ErrorMessage);return enabled.Data!;
        }
        public McpAccessRequest Request(McpClientDto c)=>new(c.OAuthClientId,Actor.UserId,Actor.AuthVersion,McpToolCatalog.All.Select(x=>x.Name).ToArray(),false,[First,Second],"192.0.2.20",null);
    }
    private sealed class Ids:IIdService{private long _next=9_200_000_000_000_000;public long NextId()=>Interlocked.Increment(ref _next);}
    private sealed class Readiness:IMcpReadinessService{public Task<McpHostStatus> ReadAsync(CancellationToken ct)=>Task.FromResult(new McpHostStatus(true,true,"https://fixture.example/mcp",["2026-07-28","2025-11-25"],[]));}
    private sealed class Revoker:IUserSessionRevoker{public Task RevokeAsync(long userId,CancellationToken ct)=>Task.CompletedTask;}
    private sealed class Publisher:ITelemetryPublisher{public Task PublishAsync(ServerTelemetryDto snapshot,CancellationToken ct)=>throw new InvalidOperationException("查询不得发布设备更新");}
    private sealed class Recorder:IExceptionRecorder{public Task RecordAsync(Exception? exception,ExceptionLogContext context)=>throw new InvalidOperationException("查询不得生成设备操作异常");}
    private sealed class OAuth:IMcpOAuthRegistrationService
    {
        public bool Fail{get;set;}
        public Task<ServiceResult<bool>> SyncAsync(McpOAuthClientRegistration request,CancellationToken ct)=>Task.FromResult(Fail?ServiceResult<bool>.Fail("FIXTURE_OAUTH_FAILED","合成故障",503):ServiceResult<bool>.Ok(true));
        public Task<ServiceResult<bool>> RevokeAsync(string clientId,CancellationToken ct)=>Task.FromResult(ServiceResult<bool>.Ok(true));
        public Task<ServiceResult<McpSecretDto>> RotateSecretAsync(string clientId,CancellationToken ct)=>Task.FromResult(ServiceResult<McpSecretDto>.Ok(new(clientId,"synthetic-once")));
    }
    private sealed class BrokenFactory:IDbContextFactory<OobDbContext>{public OobDbContext CreateDbContext()=>throw new IOException("合成数据库不可用");}
}

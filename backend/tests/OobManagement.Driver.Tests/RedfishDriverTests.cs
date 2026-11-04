using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl.OutOfBand;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>所有写请求只进入内存处理器，测试永远不会访问真实带外地址。</summary>
public sealed class RedfishDriverTests
{
    private static readonly OutOfBandConnection Connection = new()
    {
        Endpoint = new Uri("https://bmc.test/"), Username = "test-user", Password = "test-secret"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connection_discovers_arbitrary_member_ids_and_keeps_identity_names_separate(bool hp)
    {
        var handler = new FakeBmc(hp);
        var result = await Driver(handler, hp).TestConnectionAsync(Connection);
        Assert.True(result.Success);
        Assert.Equal(hp ? "iLO4" : "iDRAC8", result.Identity.ControllerType);
        Assert.Equal("server-os", result.Identity.HostName);
        Assert.Equal("controller-host", result.Identity.ControllerHostName);
        Assert.Equal("2.82", result.Identity.FirmwareVersion);
        Assert.Contains(PowerAction.On, result.Capabilities.PowerActions);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Same_origin_absolute_member_links_are_supported()
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Systems"] = """{"Members":[{"@odata.id":"https://bmc.test/redfish/v1/Systems/node-42"}]}""";
        var result = await Driver(handler, true).TestConnectionAsync(Connection);
        Assert.True(result.Success);
        Assert.Contains("/redfish/v1/Systems/node-42", handler.Reads);
    }

    [Fact]
    public async Task Cross_origin_discovery_is_rejected_without_sending_credentials_to_other_host()
    {
        var handler = new FakeBmc();
        handler.Json["/redfish/v1/Systems"] = """{"Members":[{"@odata.id":"https://other.test/redfish/v1/Systems/1"}]}""";
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.All(handler.Hosts, host => Assert.Equal("bmc.test", host));
        Assert.DoesNotContain("test-secret", result.ErrorMessage ?? "");
    }

    [Theory]
    [InlineData(401, "AuthenticationFailed")]
    [InlineData(403, "PermissionDenied")]
    public async Task Authentication_and_permission_failures_are_not_retried(int status, string code)
    {
        var handler = new FakeBmc { Override = request => request.Method == HttpMethod.Get ? new HttpResponseMessage((HttpStatusCode)status) : null };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.Single(handler.Reads);
        Assert.Empty(handler.Writes);
        Assert.DoesNotContain("test-secret", result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task Failed_optional_manager_does_not_hide_successful_host_and_telemetry()
    {
        var handler = new FakeBmc { Override = request => request.RequestUri!.AbsolutePath == "/redfish/v1/Managers/controller-7" ? throw new HttpRequestException("untrusted content containing test-secret") : null };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.True(result.Success);
        Assert.Equal("PowerEdge R630", result.Identity.Model);
        Assert.Null(result.Identity.FirmwareVersion);
        Assert.NotEmpty(result.Warnings);
        Assert.All(result.Warnings, warning => Assert.DoesNotContain("test-secret", warning));
    }

    [Fact]
    public async Task Inventory_keeps_controller_drive_relationships_cpu_memory_and_battery()
    {
        var result = await Driver(new FakeBmc(true), true).ReadInventoryAsync(Connection);
        Assert.Contains(result.Components, component => component.Kind == "Processor" && component.Model == "Xeon test");
        Assert.Contains(result.Components, component => component.Kind == "Memory" && component.CapacityBytes == 16L * 1024 * 1024 * 1024);
        Assert.Contains(result.Components, component => component.Kind == "Battery" && component.Model == "Smart Storage Battery");
        var controller = Assert.Single(result.Components, component => component.Kind == "StorageController" && component.Model == "P440ar");
        Assert.Contains(controller.Children, component => component.Kind == "Drive" && component.SerialNumber == "disk-serial");
        Assert.Contains(controller.Children, component => component.Kind == "Battery" && component.Status == "PresentAndCharged");
    }

    [Fact]
    public async Task Failed_single_disk_does_not_drop_other_hardware()
    {
        var handler = new FakeBmc(true) { Override = request => request.RequestUri!.AbsolutePath.EndsWith("/Drives/disk-8", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null };
        var result = await Driver(handler, true).ReadInventoryAsync(Connection);
        Assert.Contains(result.Components, component => component.Kind == "Processor");
        Assert.Contains(result.Components, component => component.Kind == "Memory");
        Assert.Contains(result.Warnings, warning => warning.Contains("物理磁盘", StringComparison.Ordinal));
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Telemetry_keeps_nulls_absent_sensors_units_and_one_unified_fan_percentage()
    {
        var result = await Driver(new FakeBmc(true), true).ReadTelemetryAsync(Connection);
        Assert.Equal("On", result.Identity?.PowerState);
        Assert.Equal(126, Assert.Single(result.Metrics, metric => metric.MetricCode == "power.watts").Value);
        Assert.Equal(14d, Assert.Single(result.Metrics, metric => metric.MetricCode == "power.capacity.percent").Value!.Value, 6);
        var btu = Assert.Single(result.Metrics, metric => metric.MetricCode == "power.btuPerHour");
        Assert.Equal("Derived", btu.Source);
        Assert.Equal(429.929845758, btu.Value!.Value, 6);
        var unavailable = Assert.Single(result.Metrics, metric => metric.SensorKey == "temperature:missing");
        Assert.Null(unavailable.Value);
        Assert.Equal("TemporarilyUnavailable", unavailable.Availability);
        var absent = Assert.Single(result.Metrics, metric => metric.SensorKey == "temperature:absent");
        Assert.Equal("NotInstalled", absent.Availability);
        Assert.Null(absent.Value);
        Assert.Equal(25, Assert.Single(result.Metrics, metric => metric.SensorKey == "fan:percent" && metric.MetricCode == "fan.percent").Value);
        Assert.Null(Assert.Single(result.Metrics, metric => metric.SensorKey == "fan:percent" && metric.MetricCode == "fan.rpm").Value);
        Assert.Equal(4200, Assert.Single(result.Metrics, metric => metric.SensorKey == "fan:rpm" && metric.MetricCode == "fan.rpm").Value);
        Assert.DoesNotContain(result.Metrics, metric => metric.SensorKey == "fan:percent" && metric.MetricCode == "fan.pwm.percent");
        Assert.Equal(32, Assert.Single(result.Metrics, metric => metric.SensorKey == "fan:pwm" && metric.MetricCode == "fan.percent").Value);
        Assert.DoesNotContain(result.Metrics, metric => metric.MetricCode == "fan.pwm.percent");
        Assert.All(result.Metrics.Where(metric => metric.MetricCode == "fan.percent"), metric => Assert.EndsWith("转速百分比（%）", metric.Name));
        Assert.Equal("Unsupported", Assert.Single(result.Metrics, metric => metric.MetricCode == "power.energy.kwh").Availability);
    }

    [Theory]
    [InlineData(25d, 32d, 32d, "Available")]
    [InlineData(25d, 0d, 0d, "Available")]
    [InlineData(0d, null, 0d, "Available")]
    [InlineData(25d, 101d, 25d, "Available")]
    [InlineData(-1d, 42d, 42d, "Available")]
    [InlineData(101d, -1d, null, "TemporarilyUnavailable")]
    [InlineData(null, null, null, "TemporarilyUnavailable")]
    public async Task Unified_percentage_prefers_valid_native_pwm_and_keeps_zero_without_converting_rpm(double? speed, double? pwm, double? expected, string availability)
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Chassis/chassis-A/Thermal"] = JsonSerializer.Serialize(new
        {
            Fans = new[] { new { MemberId = "one", Name = "Fan 1", ReadingRPM = 4321, SpeedPercent = speed, PwmPercent = pwm } }
        });
        var result = await Driver(handler, true).ReadTelemetryAsync(Connection);
        var percent = Assert.Single(result.Metrics, metric => metric.MetricCode == "fan.percent");
        Assert.Equal("fan:one", percent.SensorKey);
        Assert.Equal(expected, percent.Value);
        Assert.Equal(availability, percent.Availability);
        Assert.Equal("%", percent.Unit);
        Assert.Equal("Fan 1转速百分比（%）", percent.Name);
        Assert.Equal(4321, Assert.Single(result.Metrics, metric => metric.MetricCode == "fan.rpm").Value);
        Assert.DoesNotContain(result.Metrics, metric => metric.MetricCode == "fan.pwm.percent");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Non_finite_native_percentage_cannot_hide_another_valid_native_percentage(string pwm)
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Chassis/chassis-A/Thermal"] = JsonSerializer.Serialize(new
        {
            Fans = new[] { new { MemberId = "one", Name = "Fan 1", SpeedPercent = 19, PwmPercent = pwm } }
        });
        var result = await Driver(handler, true).ReadTelemetryAsync(Connection);
        Assert.Equal(19, Assert.Single(result.Metrics, metric => metric.MetricCode == "fan.percent").Value);
        Assert.DoesNotContain(result.Metrics, metric => metric.MetricCode == "fan.pwm.percent");
    }

    [Fact]
    public async Task Invalid_top_level_pwm_does_not_hide_valid_oem_pwm()
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Chassis/chassis-A/Thermal"] = """{"Fans":[{"MemberId":"one","Name":"Fan 1","SpeedPercent":19,"PwmPercent":101,"Oem":{"Hp":{"PwmPercent":35}}}]}""";
        var result = await Driver(handler, true).ReadTelemetryAsync(Connection);
        Assert.Equal(35, Assert.Single(result.Metrics, metric => metric.MetricCode == "fan.percent").Value);
    }

    [Fact]
    public async Task Declared_null_oem_pwm_preserves_known_percentage_capability_as_missing()
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Chassis/chassis-A/Thermal"] = """{"Fans":[{"MemberId":"one","Name":"Fan 1","Oem":{"Hp":{"PwmPercent":null}}}]}""";
        var result = await Driver(handler, true).ReadTelemetryAsync(Connection);
        var percent = Assert.Single(result.Metrics, metric => metric.MetricCode == "fan.percent");
        Assert.Null(percent.Value);
        Assert.Equal("TemporarilyUnavailable", percent.Availability);
    }

    [Fact]
    public async Task Absent_fan_keeps_one_not_installed_percentage_instead_of_native_values()
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Chassis/chassis-A/Thermal"] = """{"Fans":[{"MemberId":"gone","Name":"Fan 1","SpeedPercent":25,"PwmPercent":32,"Status":{"State":"Absent"}}]}""";
        var result = await Driver(handler, true).ReadTelemetryAsync(Connection);
        var percent = Assert.Single(result.Metrics, metric => metric.MetricCode == "fan.percent");
        Assert.Null(percent.Value);
        Assert.Equal("NotInstalled", percent.Availability);
        Assert.DoesNotContain(result.Metrics, metric => metric.MetricCode == "fan.pwm.percent");
    }

    [Fact]
    public async Task Failed_power_resource_does_not_hide_temperature_and_fans_or_emit_zero()
    {
        var handler = new FakeBmc { Override = request => request.RequestUri!.AbsolutePath == "/redfish/v1/Chassis/chassis-A/Power" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        var power = Assert.Single(result.Metrics, metric => metric.MetricCode == "power.watts");
        Assert.Null(power.Value);
        Assert.Equal("TemporarilyUnavailable", power.Availability);
        Assert.Contains(result.Metrics, metric => metric.MetricCode == "temperature.celsius" && metric.Value == 21);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData(PowerAction.On)]
    [InlineData(PowerAction.GracefulShutdown)]
    [InlineData(PowerAction.ForceOff)]
    [InlineData(PowerAction.GracefulRestart)]
    [InlineData(PowerAction.ForceRestart)]
    [InlineData(PowerAction.PowerCycle)]
    [InlineData(PowerAction.PushPowerButton)]
    public async Task Every_supported_power_action_maps_exactly_and_sends_only_once(PowerAction action)
    {
        var handler = new FakeBmc();
        handler.ChangeSystem(system => system["PowerState"] = action == PowerAction.On ? "Off" : "On");
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, action);
        Assert.Equal("Submitted", result.Status);
        var sent = Assert.Single(handler.Writes);
        Assert.Equal("/redfish/v1/Systems/node-42/Actions/ComputerSystem.Reset", sent.Path);
        using var document = JsonDocument.Parse(sent.Body);
        Assert.Equal(action.ToString(), document.RootElement.GetProperty("ResetType").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task Unsupported_graceful_action_never_falls_back_to_force_or_power_button()
    {
        var handler = new FakeBmc();
        handler.ChangeSystem(system => system["Actions"]!["#ComputerSystem.Reset"]!["ResetType@Redfish.AllowableValues"] = JsonSerializer.SerializeToNode(new[] { "On", "ForceOff", "ForceRestart", "PushPowerButton" }));
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.GracefulRestart);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("PowerActionNotSupported", result.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Action_info_is_used_when_inline_allowable_values_are_not_present()
    {
        var handler = new FakeBmc();
        handler.ChangeSystem(system =>
        {
            var action = system["Actions"]!["#ComputerSystem.Reset"]!.AsObject();
            action.Remove("ResetType@Redfish.AllowableValues");
            action["@Redfish.ActionInfo"] = "/redfish/v1/Systems/node-42/ResetActionInfo";
        });
        handler.Json["/redfish/v1/Systems/node-42/ResetActionInfo"] = """{"Parameters":[{"Name":"ResetType","AllowableValues":["On","ForceOff"]}]}""";
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Submitted", result.Status);
        Assert.Single(handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Force_off_reads_only_the_unique_system_and_declared_action_before_sending_once(bool hp)
    {
        var handler = new FakeBmc(hp);
        var result = await Driver(handler, hp).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Submitted", result.Status);
        Assert.Equal(new[] { "/redfish/v1/", "/redfish/v1/Systems", "/redfish/v1/Systems/node-42" }, handler.Reads);
        using var body = JsonDocument.Parse(Assert.Single(handler.Writes).Body);
        Assert.Equal("ForceOff", body.RootElement.GetProperty("ResetType").GetString());
    }

    [Fact]
    public async Task Failed_action_info_read_is_not_misreported_as_unsupported_and_never_sends_power()
    {
        var handler = new FakeBmc(true) { Override = request => request.RequestUri!.AbsolutePath.EndsWith("/ResetActionInfo", StringComparison.Ordinal)
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "Fixture cannot connect.") : null };
        handler.ChangeSystem(system =>
        {
            var action = system["Actions"]!["#ComputerSystem.Reset"]!.AsObject();
            action.Remove("ResetType@Redfish.AllowableValues");
            action["@Redfish.ActionInfo"] = "/redfish/v1/Systems/node-42/ResetActionInfo";
        });
        var result = await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("ConnectionFailed", result.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false, "On")]
    [InlineData(false, "Off")]
    [InlineData(true, "On")]
    [InlineData(true, "Off")]
    public async Task Power_capability_query_reads_only_system_and_returns_declared_actions_independent_of_state(bool hp, string state)
    {
        var handler = new FakeBmc(hp);
        handler.ChangeSystem(system => system["PowerState"] = state);
        var result = await Driver(handler, hp).ReadPowerCapabilitiesAsync(Connection);
        Assert.True(result.Success);
        Assert.Equal(state, result.Identity.PowerState);
        Assert.Contains(PowerAction.ForceOff, result.Capabilities.PowerActions);
        Assert.Contains(PowerAction.On, result.Capabilities.PowerActions);
        Assert.Empty(result.Capabilities.SupportedMetrics);
        Assert.False(result.Capabilities.SupportsInventory);
        Assert.Equal(new[] { "/redfish/v1/", "/redfish/v1/Systems", "/redfish/v1/Systems/node-42" }, handler.Reads);
        Assert.Empty(handler.Writes);
        Assert.All(handler.Headers, header => Assert.Equal(hp ? true : (bool?)null, header.ConnectionClose));
    }

    [Theory]
    [InlineData("/redfish/v1/")]
    [InlineData("/redfish/v1/Systems")]
    [InlineData("/redfish/v1/Systems/node-42")]
    public async Task Power_capability_query_connection_failure_is_not_success_with_empty_actions(string failedPath)
    {
        var handler = new FakeBmc(true) { Override = request => request.RequestUri!.AbsolutePath == failedPath
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "Fixture cannot connect.") : null };
        var result = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal("ConnectionFailed", result.ErrorCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "ConnectionFailed");
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Power_capability_query_distinguishes_failed_action_info_from_successful_declaration(bool fail)
    {
        const string infoPath = "/redfish/v1/Systems/node-42/ResetActionInfo";
        var handler = new FakeBmc(true) { Override = request => fail && request.RequestUri!.AbsolutePath == infoPath
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "Fixture cannot connect.") : null };
        handler.ChangeSystem(system =>
        {
            var action = system["Actions"]!["#ComputerSystem.Reset"]!.AsObject();
            action.Remove("ResetType@Redfish.AllowableValues");
            action["@Redfish.ActionInfo"] = infoPath;
        });
        handler.Json[infoPath] = """{"Parameters":[{"Name":"ResetType","AllowableValues":["On","ForceOff"]}]}""";
        var result = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.Equal(!fail, result.Success);
        if (fail) Assert.Equal("ConnectionFailed", result.ErrorCode);
        else Assert.Equal(new[] { PowerAction.On, PowerAction.ForceOff }, result.Capabilities.PowerActions);
        Assert.Equal(new[] { "/redfish/v1/", "/redfish/v1/Systems", "/redfish/v1/Systems/node-42", infoPath }, handler.Reads);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Power_capability_query_success_without_action_declaration_returns_known_empty_actions()
    {
        var handler = new FakeBmc(true);
        handler.ChangeSystem(system => system.Remove("Actions"));
        var result = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.True(result.Success);
        Assert.Empty(result.Capabilities.PowerActions);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData("missing-target")]
    [InlineData("wrong-target-type")]
    [InlineData("other-target")]
    [InlineData("missing-values")]
    [InlineData("wrong-values")]
    [InlineData("wrong-action")]
    [InlineData("wrong-actions")]
    public async Task Malformed_power_declaration_is_a_read_failure_instead_of_known_unsupported(string shape)
    {
        var handler = new FakeBmc(true);
        handler.ChangeSystem(system =>
        {
            var action = system["Actions"]!["#ComputerSystem.Reset"]!.AsObject();
            switch (shape)
            {
                case "missing-target": action.Remove("target"); break;
                case "wrong-target-type": action["target"] = new JsonObject(); break;
                case "other-target": action["target"] = "/redfish/v1/Systems/other/Actions/ComputerSystem.Reset"; break;
                case "missing-values": action.Remove("ResetType@Redfish.AllowableValues"); break;
                case "wrong-values": action["ResetType@Redfish.AllowableValues"] = "ForceOff"; break;
                case "wrong-action": system["Actions"]!["#ComputerSystem.Reset"] = new JsonArray(); break;
                case "wrong-actions": system["Actions"] = new JsonArray(); break;
            }
        });
        var query = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.False(query.Success);
        Assert.NotNull(query.ErrorCode);
        var execute = await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Rejected", execute.Status);
        Assert.NotEqual("PowerActionNotSupported", execute.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Parameters":{}}""")]
    [InlineData("""{"Parameters":[]}""")]
    [InlineData("""{"Parameters":[{"Name":"ResetType"}]}""")]
    [InlineData("""{"Parameters":[{"Name":"ResetType","AllowableValues":"ForceOff"}]}""")]
    [InlineData("""{"Parameters":[{"Name":"ResetType","AllowableValues":[null]}]}""")]
    public async Task Malformed_declared_action_info_is_a_read_failure_instead_of_empty_success(string payload)
    {
        var handler = new FakeBmc(true);
        handler.ChangeSystem(system =>
        {
            var action = system["Actions"]!["#ComputerSystem.Reset"]!.AsObject();
            action.Remove("ResetType@Redfish.AllowableValues");
            action["@Redfish.ActionInfo"] = "/redfish/v1/Systems/node-42/ResetActionInfo";
        });
        handler.Json["/redfish/v1/Systems/node-42/ResetActionInfo"] = payload;
        var query = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.False(query.Success);
        Assert.Equal("InvalidPowerCapabilities", query.ErrorCode);
        var execute = await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Rejected", execute.Status);
        Assert.Equal("InvalidPowerCapabilities", execute.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Members":{}}""")]
    [InlineData("""{"Members":[{}]}""")]
    [InlineData("""{"Members@odata.count":2,"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"}]}""")]
    [InlineData("""{"Members@odata.count":0,"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"}]}""")]
    [InlineData("""{"Members@odata.count":"1","Members":[{"@odata.id":"/redfish/v1/Systems/node-42"}]}""")]
    [InlineData("""{"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"}],"Members@odata.nextLink":null}""")]
    [InlineData("""{"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"}],"Members@odata.nextLink":{}}""")]
    [InlineData("""{"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"}],"@odata.nextLink":" "}""")]
    [InlineData("""{"Items":[{"href":"/redfish/v1/Systems/node-42"}],"links":{"NextPage":{"href":null}}}""")]
    [InlineData("""{"Items":[{"href":"/redfish/v1/Systems/node-42"}],"links":{"NextPage":{"href":5}}}""")]
    public async Task Power_discovery_rejects_malformed_or_incomplete_system_collection(string payload)
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Systems"] = payload;
        var query = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.False(query.Success);
        Assert.Equal("InvalidSystemCollection", query.ErrorCode);
        var execute = await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Rejected", execute.Status);
        Assert.Equal("InvalidSystemCollection", execute.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_empty_power_values_are_known_empty_not_a_failed_read(bool actionInfo)
    {
        var handler = new FakeBmc(true);
        handler.ChangeSystem(system =>
        {
            var action = system["Actions"]!["#ComputerSystem.Reset"]!.AsObject();
            action["@Redfish.ActionInfo"] = "/redfish/v1/Systems/node-42/ResetActionInfo";
            if (actionInfo) action.Remove("ResetType@Redfish.AllowableValues");
            else action["ResetType@Redfish.AllowableValues"] = new JsonArray();
        });
        handler.Json["/redfish/v1/Systems/node-42/ResetActionInfo"] = """{"Parameters":[{"Name":"ResetType","AllowableValues":[]}]}""";
        var query = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.True(query.Success);
        Assert.Empty(query.Capabilities.PowerActions);
        Assert.Empty(query.Diagnostics);
        Assert.Equal(actionInfo, handler.Reads.Any(value => value.EndsWith("/ResetActionInfo", StringComparison.Ordinal)));
        var execute = await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("PowerActionNotSupported", execute.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Power_discovery_preserves_members_and_legacy_items_with_optional_declared_count(bool legacyItems, bool declaredCount)
    {
        var handler = new FakeBmc(true);
        var collection = new JsonObject
        {
            [legacyItems ? "Items" : "Members"] = new JsonArray(new JsonObject { [legacyItems ? "href" : "@odata.id"] = "/redfish/v1/Systems/node-42" })
        };
        if (declaredCount) collection[legacyItems ? "Total" : "Members@odata.count"] = 1;
        handler.Json["/redfish/v1/Systems"] = collection.ToJsonString();
        var query = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.True(query.Success);
        Assert.Contains(PowerAction.ForceOff, query.Capabilities.PowerActions);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Power_discovery_reads_all_declared_pages_before_accepting_the_unique_system()
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Systems"] = """{"Members@odata.count":1,"Members":[],"Members@odata.nextLink":"/redfish/v1/Systems?page=2"}""";
        handler.Json["/redfish/v1/Systems?page=2"] = """{"Members@odata.count":1,"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"}]}""";
        var query = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.True(query.Success);
        Assert.Contains("/redfish/v1/Systems?page=2", handler.Reads);
        Assert.Contains(PowerAction.ForceOff, query.Capabilities.PowerActions);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Power_discovery_fetches_a_named_member_instead_of_mistaking_its_summary_for_the_full_system()
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Systems"] = """{"Members":[{"@odata.id":"/redfish/v1/Systems/node-42","Name":"System summary"}]}""";
        var query = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.True(query.Success);
        Assert.Contains(PowerAction.ForceOff, query.Capabilities.PowerActions);
        Assert.Contains("/redfish/v1/Systems/node-42", handler.Reads);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Power_discovery_does_not_treat_an_empty_member_resource_as_known_empty_capabilities()
    {
        var handler = new FakeBmc(true);
        handler.Json["/redfish/v1/Systems/node-42"] = "{}";
        var query = await Driver(handler, true).ReadPowerCapabilitiesAsync(Connection);
        Assert.False(query.Success);
        Assert.Equal("InvalidSystemCollection", query.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData("/redfish/v1/Managers/controller-7/Actions/Manager.Reset")]
    [InlineData("/redfish/v1/Systems/other-host/Actions/ComputerSystem.Reset")]
    [InlineData("https://other.test/redfish/v1/Systems/node-42/Actions/ComputerSystem.Reset")]
    public async Task Manager_reset_other_host_and_cross_origin_targets_are_never_called(string target)
    {
        var handler = new FakeBmc();
        handler.ChangeSystem(system => system["Actions"]!["#ComputerSystem.Reset"]!["target"] = target);
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceRestart);
        Assert.Equal("Rejected", result.Status);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Trailing_slash_action_targets_are_supported_without_changing_meaning()
    {
        var handler = new FakeBmc();
        handler.ChangeSystem(system => system["PowerState"] = "Off");
        handler.ChangeSystem(system => system["Actions"]!["#ComputerSystem.Reset"]!["target"] = "/redfish/v1/Systems/node-42/Actions/ComputerSystem.Reset/");
        Assert.Equal("Submitted", (await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.On)).Status);
        Assert.Single(handler.Writes);
    }

    [Theory]
    [InlineData(401, "Rejected")]
    [InlineData(403, "Rejected")]
    [InlineData(400, "Rejected")]
    [InlineData(500, "Unknown")]
    [InlineData(408, "Unknown")]
    [InlineData(307, "Rejected")]
    public async Task Failed_power_commands_are_classified_without_retry(int status, string expected)
    {
        var handler = new FakeBmc { Override = request => request.Method == HttpMethod.Post ? new HttpResponseMessage((HttpStatusCode)status) : null };
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceRestart);
        Assert.Equal(expected, result.Status);
        Assert.Single(handler.Writes);
    }

    [Fact]
    public async Task Power_timeout_remains_unknown_and_is_never_resent()
    {
        var handler = new FakeBmc { Override = request => request.Method == HttpMethod.Post ? throw new TaskCanceledException("test-secret") : null };
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.GracefulShutdown);
        Assert.Equal("Unknown", result.Status);
        Assert.Single(handler.Writes);
        Assert.DoesNotContain("test-secret", result.Message ?? "");
    }

    [Fact]
    public async Task Read_timeout_is_safe_and_does_not_retry_authentication()
    {
        var handler = new FakeBmc { Override = _ => throw new TaskCanceledException("test-secret") };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal("Timeout", result.ErrorCode);
        Assert.Single(handler.Reads);
        Assert.DoesNotContain("test-secret", result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task Invalid_enum_is_rejected_before_any_write()
    {
        var handler = new FakeBmc();
        Assert.Equal("Rejected", (await Driver(handler).ExecutePowerActionAsync(Connection, (PowerAction)999)).Status);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Declared_null_percent_is_missing_reading_instead_of_unsupported()
    {
        var handler = new FakeBmc();
        handler.Json["/redfish/v1/Chassis/chassis-A/Thermal"] = """{"Fans":[{"MemberId":"fan-null","Name":"Fan","CurrentReading":null,"Units":"Percent"}]}""";
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        var reading = Assert.Single(result.Metrics, metric => metric.MetricCode == "fan.percent");
        Assert.Null(reading.Value);
        Assert.Equal("TemporarilyUnavailable", reading.Availability);
        Assert.Equal("Unsupported", Assert.Single(result.Metrics, metric => metric.MetricCode == "fan.rpm").Availability);
    }

    [Fact]
    public async Task Explicit_error_body_on_success_status_is_not_reported_as_accepted()
    {
        var handler = new FakeBmc { Override = request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"error":{"message":"test-secret"}}""") } : null };
        handler.ChangeSystem(system => system["PowerState"] = "Off");
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.On);
        Assert.Equal("Rejected", result.Status);
        Assert.Single(handler.Writes);
        Assert.DoesNotContain("test-secret", result.Message ?? "");
    }

    [Theory]
    [InlineData(true, "Submitted")]
    [InlineData(false, "Rejected")]
    public async Task Ilo_success_message_envelope_is_accepted_only_for_ilo_without_resending(bool hp, string expected)
    {
        var handler = new FakeBmc(hp) { Override = request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"error":{"code":"iLO.0.10.ExtendedInfo","@Message.ExtendedInfo":[{"MessageId":"Base.1.0.Success","Severity":"OK"}]}}""") } : null };
        var result = await Driver(handler, hp).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal(expected, result.Status);
        Assert.Single(handler.Writes);
    }

    [Theory]
    [InlineData("""{"error":{"code":"iLO.0.10.ExtendedInfo","@Message.ExtendedInfo":[{"MessageId":"Base.1.0.Success"},{"MessageId":"Base.1.0.GeneralError"}]}}""")]
    [InlineData("""{"Messages":[{"MessageID":"Base.1.0.GeneralError"}]}""")]
    [InlineData("""{"error":{"@Message.ExtendedInfo":[{"MessageId":"Base.1.0.Success","Severity":"Critical"}]}}""")]
    public async Task Ilo_power_error_or_mixed_messages_remain_rejected_without_resending(string payload)
    {
        var handler = new FakeBmc(true) { Override = request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) } : null };
        var result = await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Rejected", result.Status);
        Assert.Single(handler.Writes);
    }

    [Fact]
    public async Task Malformed_power_response_is_unknown_and_never_retried()
    {
        var handler = new FakeBmc { Override = request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") } : null };
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Unknown", result.Status);
        Assert.Single(handler.Writes);
    }

    [Fact]
    public async Task Multiple_systems_require_explicit_selection_before_power_operation()
    {
        var handler = new FakeBmc();
        handler.Json["/redfish/v1/Systems"] = """{"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"},{"@odata.id":"/redfish/v1/Systems/node-43"}]}""";
        handler.Json["/redfish/v1/Systems/node-43"] = """{"Id":"node-43","Model":"Another server"}""";
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.On);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("AmbiguousSystem", result.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Device_error_fields_never_become_hardware_attributes()
    {
        var handler = new FakeBmc();
        var cpu = JsonNode.Parse(handler.Json["/processors/cpu-A"])!.AsObject();
        cpu["Secret"] = "test-secret";
        cpu["Oem"] = JsonNode.Parse("""{"Dell":{"DellProcessor":{"AccessToken":"test-secret","TotalCores":8}}}""");
        handler.Json["/processors/cpu-A"] = cpu.ToJsonString();
        var inventory = await Driver(handler).ReadInventoryAsync(Connection);
        var component = Assert.Single(inventory.Components, item => item.Kind == "Processor");
        Assert.DoesNotContain(component.Attributes, attribute => attribute.Value == "test-secret");
        Assert.Contains(component.Attributes, attribute => attribute.Key == "Oem.Dell.DellProcessor.TotalCores");
    }

    [Fact]
    public async Task Invalid_certificate_fingerprint_is_rejected_before_network_access()
    {
        var connection = Connection with { CertificateFingerprint = "not-a-sha256-fingerprint" };
        var result = await new Idrac8Service().TestConnectionAsync(connection);
        Assert.False(result.Success);
        Assert.Equal("InvalidCertificateFingerprint", result.ErrorCode);
    }

    [Theory]
    [InlineData("http://bmc.test/")]
    [InlineData("https://user:password@bmc.test/")]
    [InlineData("https://bmc.test/?query=private")]
    [InlineData("https://bmc.test/#fragment")]
    [InlineData("relative-path")]
    public async Task Invalid_connection_is_a_business_result_before_http_client_creation(string endpoint)
    {
        var clients = 0;
        var driver = new Idrac8Service(_ => { clients++; return new HttpClient(new FakeBmc()); });
        var result = await driver.TestConnectionAsync(Connection with { Endpoint = new Uri(endpoint, UriKind.RelativeOrAbsolute) });
        Assert.False(result.Success);
        Assert.Equal("InvalidEndpoint", result.ErrorCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Business", diagnostic.Category);
        Assert.Null(diagnostic.Cause);
        Assert.Equal(0, clients);
    }

    [Theory]
    [InlineData(400, "DeviceRequestFailed", "Business")]
    [InlineData(401, "AuthenticationFailed", "Business")]
    [InlineData(403, "PermissionDenied", "Business")]
    [InlineData(404, "NotSupported", "Business")]
    [InlineData(429, "RateLimited", "DeviceFailure")]
    [InlineData(500, "DeviceRequestFailed", "DeviceFailure")]
    public async Task Explicit_protocol_rejections_keep_codes_without_manufacturing_exception_causes(int status, string code, string category)
    {
        var handler = new FakeBmc { Override = _ => new HttpResponseMessage((HttpStatusCode)status) };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(category, diagnostic.Category);
        Assert.Null(diagnostic.Cause);
        Assert.Single(handler.Reads);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, "TlsConnectionFailed")]
    [InlineData(HttpRequestError.ConnectionError, "ConnectionFailed")]
    public async Task Native_fault_cause_identity_is_preserved_only_in_backend_memory(HttpRequestError kind, string code)
    {
        var cause = new HttpRequestException(kind, "fixture-secret must not escape", new IOException("private inner detail"));
        var handler = new FakeBmc { Override = _ => throw cause };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DeviceFailure", diagnostic.Category);
        Assert.Equal(code, diagnostic.Code);
        Assert.Same(cause, diagnostic.Cause);
        var json = JsonStorage.Write(result);
        Assert.DoesNotContain("fixture-secret", json);
        Assert.DoesNotContain("private inner detail", json);
        Assert.DoesNotContain("diagnostics", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Optional_native_fault_keeps_partial_data_and_original_cause_for_recording()
    {
        var cause = new IOException("fixture-secret stream failure");
        var handler = new FakeBmc { Override = request => request.RequestUri!.AbsolutePath == "/redfish/v1/Managers/controller-7" ? throw cause : null };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.NotEmpty(result.Metrics);
        Assert.Equal("PowerEdge R630", result.Identity?.Model);
        Assert.Same(cause, Assert.Single(result.Diagnostics, diagnostic => diagnostic.Cause is not null).Cause);
        Assert.All(result.Warnings, warning => Assert.DoesNotContain("fixture-secret", warning));
    }

    [Fact]
    public async Task Malformed_read_json_preserves_native_parse_exception_and_safe_result()
    {
        var handler = new FakeBmc();
        handler.Json["/redfish/v1/"] = "fixture-secret invalid-json";
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal("InvalidResponse", result.ErrorCode);
        Assert.IsAssignableFrom<JsonException>(Assert.Single(result.Diagnostics).Cause);
        Assert.DoesNotContain("fixture-secret", JsonStorage.Write(result));
    }

    [Fact]
    public void All_driver_result_diagnostics_and_native_causes_are_excluded_from_persistent_contracts()
    {
        DeviceDiagnostic[] diagnostics = [new("Fault", "安全摘要", "DeviceFailure", new IOException("fixture-secret"))];
        object[] results = [new ConnectionTestResult { Diagnostics = diagnostics }, new DeviceCapabilities { Diagnostics = diagnostics },
            new DeviceInventory { Diagnostics = diagnostics }, new TelemetryReading { Diagnostics = diagnostics },
            new PowerCommandResult { Status = "Unknown", Diagnostics = diagnostics }];
        foreach (var result in results)
        {
            var json = JsonSerializer.Serialize(result, result.GetType(), JsonStorage.Options);
            Assert.DoesNotContain("diagnostics", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fixture-secret", json);
        }
        Assert.DoesNotContain("fixture-secret", JsonStorage.Write(diagnostics[0]));
        Assert.DoesNotContain("cause", JsonStorage.Write(diagnostics[0]), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("#/slots/A~1B")]
    [InlineData("#/slots/")]
    [InlineData("#/slots/~0")]
    public async Task Json_pointer_preserves_escaped_and_empty_member_names(string pointer)
    {
        var handler = new FakeBmc();
        var cpu = handler.Json["/processors/cpu-A"];
        handler.Json["/processors"] = JsonSerializer.Serialize(new { Members = new[] { new Dictionary<string, string> { ["@odata.id"] = "/cpu-document" + pointer } } });
        handler.Json["/cpu-document"] = "{\"slots\":{\"A/B\":" + cpu + ",\"\":" + cpu + ",\"~\":" + cpu + "}}";
        var inventory = await Driver(handler).ReadInventoryAsync(Connection);
        Assert.Contains(inventory.Components, component => component.Kind == "Processor" && component.Model == "Xeon test");
    }

    [Theory]
    [InlineData("#/slots/missing")]
    [InlineData("#/slots/~2")]
    public async Task Invalid_json_pointer_returns_structured_protocol_failure_and_preserves_other_inventory(string pointer)
    {
        var handler = new FakeBmc();
        handler.Json["/processors"] = JsonSerializer.Serialize(new { Members = new[] { new Dictionary<string, string> { ["@odata.id"] = "/cpu-document" + pointer } } });
        handler.Json["/cpu-document"] = "{\"slots\":{}}";
        var inventory = await Driver(handler).ReadInventoryAsync(Connection);
        Assert.Contains(inventory.Components, component => component.Kind == "Memory");
        var diagnostic = Assert.Single(inventory.Diagnostics, item => item.Code == "InvalidResourcePointer");
        Assert.Equal("DeviceFailure", diagnostic.Category);
        Assert.Null(diagnostic.Cause);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Power_network_fault_preserves_original_cause_and_unknown_result_without_resend()
    {
        var cause = new HttpRequestException("fixture-secret");
        var handler = new FakeBmc { Override = request => request.Method == HttpMethod.Post ? throw cause : null };
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Unknown", result.Status);
        Assert.Same(cause, Assert.Single(result.Diagnostics).Cause);
        Assert.Single(handler.Writes);
        Assert.DoesNotContain("fixture-secret", JsonStorage.Write(result));
    }

    [Fact]
    public async Task Ilo_power_command_closes_discovery_and_post_connections_before_the_next_request()
    {
        var handler = new FakeBmc(true) { FailReusedPowerConnection = true };
        handler.ChangeSystem(system => system["PowerState"] = "Off");
        var result = await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.On);
        Assert.Equal("Submitted", result.Status);
        Assert.Single(handler.Writes);
        Assert.NotEmpty(handler.Reads);
        Assert.All(handler.Headers, header => Assert.True(header.ConnectionClose));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Power_response_interruption_stays_unknown_and_sends_only_once_even_with_connection_close(bool hp)
    {
        var cause = new HttpRequestException(HttpRequestError.ResponseEnded, "Fixture power response ended prematurely.",
            new HttpIOException(HttpRequestError.ResponseEnded, "Fixture incomplete response."));
        var handler = new FakeBmc(hp) { Override = request => request.Method == HttpMethod.Post ? throw cause : null };
        handler.ChangeSystem(system => system["PowerState"] = "Off");
        var result = await Driver(handler, hp).ExecutePowerActionAsync(Connection, PowerAction.On);
        Assert.Equal("Unknown", result.Status);
        Assert.Equal("PowerResultUnknown", result.ErrorCode);
        Assert.Same(cause, Assert.Single(result.Diagnostics).Cause);
        Assert.Single(handler.Writes);
        Assert.All(handler.Headers, header => Assert.Equal(hp ? true : (bool?)null, header.ConnectionClose));
    }

    [Fact]
    public async Task Power_caller_cancellation_after_send_stays_unknown_without_error_diagnostic_or_resend()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeBmc { Override = request =>
        {
            if (request.Method != HttpMethod.Post) return null;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        } };
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceOff, cancellation.Token);
        Assert.Equal("Unknown", result.Status);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Category == "DeviceFailure");
        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "PowerWaitCancelled").Cause);
        Assert.Single(handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Power_cancellation_before_send_returns_rejected_and_retains_cancel_cause_without_manufacturing_fault(bool actionInfo)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeBmc { Override = request =>
        {
            if (request.RequestUri!.AbsolutePath != (actionInfo ? "/power-info" : "/redfish/v1/")) return null;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        } };
        if (actionInfo) handler.ChangeSystem(system =>
        {
            var action = system["Actions"]!["#ComputerSystem.Reset"]!.AsObject();
            action.Remove("ResetType@Redfish.AllowableValues");
            action["@Redfish.ActionInfo"] = "/power-info";
        });
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceOff, cancellation.Token);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("PowerNotSentCancelled", result.ErrorCode);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Business", diagnostic.Category);
        Assert.IsAssignableFrom<OperationCanceledException>(diagnostic.Cause);
        Assert.Empty(handler.Writes);
    }

    [Fact]
    public async Task Incomplete_system_collection_cannot_infer_a_unique_power_target()
    {
        var handler = new FakeBmc();
        handler.Json["/redfish/v1/Systems"] = """{"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"},{"@odata.id":"/redfish/v1/Systems/inaccessible"}]}""";
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("NotSupported", result.ErrorCode);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_response_is_a_structured_fault_and_power_result_remains_unknown(bool power)
    {
        var handler = new FakeBmc { Override = request =>
        {
            if ((request.Method == HttpMethod.Post) != power) return null;
            var content = new StringContent("{}");
            content.Headers.ContentLength = 8 * 1024 * 1024 + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        } };
        IReadOnlyList<DeviceDiagnostic> diagnostics;
        if (power)
        {
            var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceOff);
            Assert.Equal("Unknown", result.Status);
            diagnostics = result.Diagnostics;
        }
        else
        {
            var result = await Driver(handler).TestConnectionAsync(Connection);
            Assert.False(result.Success);
            diagnostics = result.Diagnostics;
        }
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("ResponseTooLarge", diagnostic.Code);
        Assert.Equal("DeviceFailure", diagnostic.Category);
        Assert.Null(diagnostic.Cause);
        if (power) Assert.Single(handler.Writes);
        else Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, "TlsConnectionFailed")]
    [InlineData(HttpRequestError.ConnectionError, "ConnectionFailed")]
    public async Task Tls_and_network_failures_have_distinct_safe_codes(HttpRequestError error, string code)
    {
        var handler = new FakeBmc { Override = _ => throw new HttpRequestException(error, "fixture-secret must never appear", null) };
        var result = await Driver(handler, true).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.DoesNotContain("fixture-secret", result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task Ilo_compatibility_applies_only_to_reads_and_preserves_standard_power_actions()
    {
        var handler = new FakeBmc(true);
        await Driver(handler, true).ReadTelemetryAsync(Connection);
        Assert.All(handler.Headers, header => Assert.Null(header.ODataVersion));
        Assert.All(handler.Headers, header => Assert.Null(header.ConnectionClose));
        handler.ChangeSystem(system => system["PowerState"] = "Off");
        await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.On);
        Assert.Equal("4.0", Assert.Single(handler.Headers, header => header.Method == HttpMethod.Post).ODataVersion);
        Assert.Single(handler.Writes);
    }

    [Fact]
    public async Task Dell_reads_remain_in_standard_redfish_mode()
    {
        var handler = new FakeBmc();
        await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.All(handler.Headers, header => Assert.Equal("4.0", header.ODataVersion));
        Assert.All(handler.Headers, header => Assert.Null(header.ConnectionClose));
    }

    [Theory]
    [InlineData("On")]
    [InlineData("Off")]
    [InlineData("Unknown")]
    [InlineData("PoweringOn")]
    [InlineData("PoweringOff")]
    [InlineData(null)]
    public async Task Every_action_requires_its_explicit_current_power_state(string? state)
    {
        foreach (var action in Enum.GetValues<PowerAction>())
        {
            var handler = new FakeBmc();
            handler.ChangeSystem(system => { if (state is null) system.Remove("PowerState"); else system["PowerState"] = state; });
            var result = await Driver(handler).ExecutePowerActionAsync(Connection, action);
            var permitted = action switch { PowerAction.On => state == "Off", PowerAction.PushPowerButton => state is "On" or "Off", _ => state == "On" };
            Assert.Equal(permitted ? "Submitted" : "Rejected", result.Status);
            Assert.Equal(permitted ? 1 : 0, handler.Writes.Count);
            if (!permitted) Assert.Equal("PowerStateConflict", result.ErrorCode);
        }
    }

    [Fact]
    public async Task Capability_list_does_not_hide_supported_actions_because_host_is_off()
    {
        var handler = new FakeBmc();
        handler.ChangeSystem(system => system["PowerState"] = "Off");
        var capabilities = await Driver(handler).GetCapabilitiesAsync(Connection);
        Assert.Contains(PowerAction.ForceRestart, capabilities.PowerActions);
        Assert.Empty(handler.Writes);
    }

    [Theory]
    [InlineData(false, "On")]
    [InlineData(false, "Off")]
    [InlineData(true, "On")]
    [InlineData(true, "Off")]
    public async Task Declared_short_power_button_is_distinct_from_graceful_shutdown_and_long_press(bool hp, string state)
    {
        var handler = new FakeBmc(hp);
        handler.ChangeSystem(system =>
        {
            system["PowerState"] = state;
            system["Actions"]!["#ComputerSystem.Reset"]!["ResetType@Redfish.AllowableValues"] = JsonSerializer.SerializeToNode(new[] { "On", "PushPowerButton", "ForceOff" });
        });
        var capabilities = await Driver(handler, hp).GetCapabilitiesAsync(Connection);
        Assert.Contains(capabilities.PowerActions, action => action.ToString() == "PushPowerButton");
        Assert.DoesNotContain(PowerAction.GracefulShutdown, capabilities.PowerActions);
        Assert.Empty(handler.Writes);
        var shortPress = Enum.Parse<PowerAction>("PushPowerButton");
        var result = await Driver(handler, hp).ExecutePowerActionAsync(Connection, shortPress);
        Assert.Equal("Submitted", result.Status);
        using var document = JsonDocument.Parse(Assert.Single(handler.Writes).Body);
        Assert.Equal("PushPowerButton", document.RootElement.GetProperty("ResetType").GetString());
    }

    [Fact]
    public async Task Ilo_282_declared_reset_actions_with_trailing_slashes_are_available_without_exposing_nmi()
    {
        // 复现 iLO4 2.82 的只读声明；资源编号替换为夹具值，不把型号当作默认能力。
        var handler = new FakeBmc(true);
        handler.ChangeSystem(system =>
        {
            system["@odata.id"] = "/redfish/v1/Systems/node-42/";
            system["PowerState"] = "Off";
            system["Actions"] = JsonNode.Parse("""{"#ComputerSystem.Reset":{"ResetType@Redfish.AllowableValues":["On","ForceOff","ForceRestart","Nmi","PushPowerButton"],"target":"/redfish/v1/Systems/node-42/Actions/ComputerSystem.Reset/"}}""");
        });
        var capabilities = await Driver(handler, true).GetCapabilitiesAsync(Connection);
        Assert.Equal(new[] { PowerAction.On, PowerAction.ForceOff, PowerAction.ForceRestart, PowerAction.PushPowerButton }, capabilities.PowerActions);
        Assert.Empty(handler.Writes);
        Assert.Equal("Submitted", (await Driver(handler, true).ExecutePowerActionAsync(Connection, PowerAction.On)).Status);
        Assert.Equal("/redfish/v1/Systems/node-42/Actions/ComputerSystem.Reset/", Assert.Single(handler.Writes).Path);
    }

    private static IOutOfBandService Driver(FakeBmc handler, bool hp = false) => hp
        ? new Ilo4Service(_ => new HttpClient(handler, disposeHandler: false))
        : new Idrac8Service(_ => new HttpClient(handler, disposeHandler: false));

    private sealed class FakeBmc : HttpMessageHandler
    {
        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];
        public List<string> Hosts { get; } = [];
        public List<(string Path, string Body)> Writes { get; } = [];
        public List<(HttpMethod Method, string? ODataVersion, bool? ConnectionClose)> Headers { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; init; }
        public bool FailReusedPowerConnection { get; init; }
        private bool _reusableGetConnection;

        public FakeBmc(bool hp = false)
        {
            Json["/redfish/v1/"] = """{"Systems":{"@odata.id":"/redfish/v1/Systems"},"Managers":{"@odata.id":"/redfish/v1/Managers"},"Chassis":{"@odata.id":"/redfish/v1/Chassis"}}""";
            Json["/redfish/v1/Systems"] = """{"Members":[{"@odata.id":"/redfish/v1/Systems/node-42"}]}""";
            Json["/redfish/v1/Systems/node-42"] = """{"@odata.id":"/redfish/v1/Systems/node-42","Manufacturer":"Dell Inc.","Model":"PowerEdge R630","SerialNumber":"test-serial","HostName":"server-os","PowerState":"On","Processors":{"@odata.id":"/processors"},"Memory":{"@odata.id":"/memory"},"Links":{"Chassis":[{"@odata.id":"/redfish/v1/Chassis/chassis-A"}]},"Actions":{"#ComputerSystem.Reset":{"target":"/redfish/v1/Systems/node-42/Actions/ComputerSystem.Reset","ResetType@Redfish.AllowableValues":["On","GracefulShutdown","ForceOff","GracefulRestart","ForceRestart","PowerCycle","PushPowerButton"]}}} """;
            if (hp) ChangeSystem(system =>
            {
                system["Manufacturer"] = "HP";
                system["Model"] = "ProLiant DL360p Gen8";
                system["Oem"] = JsonNode.Parse("""{"Hp":{"Battery":[{"Id":"battery1","Name":"Smart Battery","Model":"Smart Storage Battery","Condition":"OK"}],"links":{"SmartStorage":{"href":"/smart-storage"}}}}""");
            });
            Json["/redfish/v1/Managers"] = """{"Members":[{"@odata.id":"/redfish/v1/Managers/controller-7"}]}""";
            Json["/redfish/v1/Managers/controller-7"] = """{"FirmwareVersion":"2.82","HostName":"controller-host"}""";
            Json["/redfish/v1/Chassis/chassis-A"] = """{"Power":{"@odata.id":"/redfish/v1/Chassis/chassis-A/Power"},"Thermal":{"@odata.id":"/redfish/v1/Chassis/chassis-A/Thermal"}}""";
            Json["/redfish/v1/Chassis/chassis-A/Power"] = """{"PowerControl":[{"PowerConsumedWatts":126,"PowerCapacityWatts":900,"PowerMetrics":{"AverageConsumedWatts":130,"MaxConsumedWatts":180,"MinConsumedWatts":90,"IntervalInMin":5}}],"PowerSupplies":[{"MemberId":"1","Name":"PSU 1","LastPowerOutputWatts":63,"PowerInputWatts":70,"PowerCapacityWatts":900,"LineInputVoltage":220}]}""";
            Json["/redfish/v1/Chassis/chassis-A/Thermal"] = """{"Temperatures":[{"MemberId":"inlet","Name":"Inlet","ReadingCelsius":21},{"MemberId":"missing","Name":"Missing","ReadingCelsius":null},{"MemberId":"absent","Name":"Uninstalled","ReadingCelsius":30,"Status":{"State":"Absent"}}],"Fans":[{"MemberId":"percent","Name":"Percent fan","Reading":25,"ReadingUnits":"Percent"},{"MemberId":"rpm","Name":"RPM fan","Reading":4200,"ReadingUnits":"RPM"},{"MemberId":"pwm","Name":"PWM fan","PwmPercent":32}]}""";
            Json["/processors"] = """{"Members":[{"@odata.id":"/processors/cpu-A"}]}""";
            Json["/processors/cpu-A"] = """{"Id":"cpu-A","Name":"CPU 1","Model":"Xeon test","TotalCores":8,"TotalThreads":16,"Status":{"Health":"OK"}}""";
            Json["/memory"] = """{"Members":[{"@odata.id":"/memory/dimm-A"}]}""";
            Json["/memory/dimm-A"] = """{"Id":"dimm-A","Name":"DIMM A","CapacityMiB":16384,"Status":{"Health":"OK"}}""";
            Json["/smart-storage"] = """{"links":{"ArrayControllers":{"href":"/controllers"}}}""";
            Json["/controllers"] = """{"Items":[{"href":"https://bmc.test/controllers/slot-A"}]}""";
            Json["/controllers/slot-A"] = """{"Id":"slot-A","Name":"Controller A","Model":"P440ar","BackupPowerSourceStatus":"PresentAndCharged","links":{"DiskDrives":{"href":"/drives"}}}""";
            Json["/drives"] = """{"Members":[{"@odata.id":"/redfish/v1/Systems/node-42/Drives/disk-8"}]}""";
            Json["/redfish/v1/Systems/node-42/Drives/disk-8"] = """{"Id":"disk-8","Name":"Bay 1","Model":"SAS test","SerialNumber":"disk-serial","CapacityGB":600,"Status":{"Health":"OK"}}""";
        }

        public void ChangeSystem(Action<JsonObject> change)
        {
            var system = JsonNode.Parse(Json["/redfish/v1/Systems/node-42"])!.AsObject();
            change(system);
            Json["/redfish/v1/Systems/node-42"] = system.ToJsonString();
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            // 此组夹具仅模拟 Redfish 能力；RACADM 认证是独立只读补充，不计入电源写动作断言。
            if (request.RequestUri.AbsolutePath.StartsWith("/cgi-bin/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            Headers.Add((request.Method, request.Headers.TryGetValues("OData-Version", out var versions) ? versions.Single() : null, request.Headers.ConnectionClose));
            if (request.Method == HttpMethod.Get)
            {
                Reads.Add(request.RequestUri.PathAndQuery);
                _reusableGetConnection = request.Headers.ConnectionClose != true;
            }
            else Writes.Add((request.RequestUri.PathAndQuery, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            // 复现发现 GET 连接回池后 POST 响应头中断；仅给 POST 加 close 不能消除前一条复用连接。
            if (request.Method == HttpMethod.Post && FailReusedPowerConnection && _reusableGetConnection)
                throw new HttpRequestException(HttpRequestError.ResponseEnded, "Fixture connection ended before power response headers.",
                    new HttpIOException(HttpRequestError.ResponseEnded, "Fixture incomplete response."));
            if (Override?.Invoke(request) is { } overridden) return overridden;
            if (request.Method == HttpMethod.Post) return new HttpResponseMessage(HttpStatusCode.NoContent);
            return Json.TryGetValue(request.RequestUri.PathAndQuery, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}

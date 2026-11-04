using System.Net;
using System.Text;
using System.Xml.Linq;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Service.impl.OutOfBand;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>以完整内存协议回归 Dell PWM 补充读取；任何电源或真实网络请求均不允许进入测试。</summary>
public sealed class DellPwmTelemetryTests
{
    private static readonly OutOfBandConnection Connection = new()
    {
        Endpoint = new Uri("https://bmc.test/"), Username = "fixture-user<&\"", Password = "fixture-secret<&\""
    };

    [Fact]
    public async Task Supplemental_pwm_matches_physical_fan_names_and_preserves_rpm_power_and_sensor_keys()
    {
        var handler = new FakeDell();
        var result = await Driver(handler).ReadTelemetryAsync(Connection);

        Assert.Equal(24, Percent(result, "1A").Value);
        Assert.Equal(37, Percent(result, "1B").Value);
        Assert.Equal("Available", Percent(result, "1A").Availability);
        Assert.Equal("%", Percent(result, "1A").Unit);
        Assert.Equal("System Board Fan1A转速百分比（%）", Percent(result, "1A").Name);
        Assert.DoesNotContain(result.Metrics, value => value.MetricCode == "fan.pwm.percent");
        Assert.Equal(2, result.Metrics.Count(value => value.MetricCode == "fan.percent"));
        Assert.Equal(5280, Rpm(result, "1A").Value);
        Assert.Equal(4920, Rpm(result, "1B").Value);
        Assert.Equal(120, Assert.Single(result.Metrics, value => value.MetricCode == "power.watts").Value);
        Assert.Equal(31, Assert.Single(result.Metrics, value => value.MetricCode == "temperature.celsius").Value);

        var login = Assert.Single(handler.Requests, value => value.Path == "/cgi-bin/login");
        var loginXml = XDocument.Parse(login.Body);
        Assert.Equal(Connection.Username, loginXml.Root!.Element("REQ")!.Element("USERNAME")!.Value);
        Assert.Equal(Connection.Password, loginXml.Root!.Element("REQ")!.Element("PASSWORD")!.Value);
        var exec = Assert.Single(handler.Requests, value => value.Path == "/cgi-bin/exec");
        var execXml = XDocument.Parse(exec.Body);
        Assert.Equal("racadm getsensorinfo", execXml.Root!.Element("REQ")!.Element("CMDINPUT")!.Value);
        Assert.Equal("0x10000", execXml.Root!.Element("REQ")!.Element("MAXOUTPUTLEN")!.Value);
        Assert.Contains("sid=owned-sid", exec.Cookie ?? "");
        AssertOwnedLogout(handler);
        AssertSafeRequests(handler);
    }

    [Fact]
    public async Task Connection_capability_detection_includes_pwm_when_racadm_returns_it()
    {
        var handler = new FakeDell();
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.True(result.Success);
        Assert.Contains("fan.percent", result.Capabilities.SupportedMetrics);
        Assert.Contains("fan.rpm", result.Capabilities.SupportedMetrics);
        Assert.DoesNotContain("fan.pwm.percent", result.Capabilities.SupportedMetrics);
        AssertOwnedLogout(handler);
        AssertSafeRequests(handler);
    }

    [Fact]
    public async Task Strict_get_only_connection_never_creates_cgi_sessions()
    {
        var handler = new FakeDell();
        var result = await Driver(handler).ReadTelemetryAsync(Connection with { UseSessionAuthentication = false });
        Assert.Equal(5280, Rpm(result, "1A").Value);
        Assert.All(handler.Requests, value => Assert.Equal(HttpMethod.Get, value.Method));
        Assert.DoesNotContain(handler.Requests, value => value.Path.StartsWith("/cgi-bin/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Existing_redfish_pwm_is_not_overwritten_and_complete_readings_skip_supplement()
    {
        var handler = new FakeDell { FirstPwm = 19, SecondPwm = 29 };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(19, Percent(result, "1A").Value);
        Assert.Equal(29, Percent(result, "1B").Value);
        Assert.DoesNotContain(handler.Requests, value => value.Path.StartsWith("/cgi-bin/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_redfish_pwm_is_preserved_while_missing_physical_fan_is_supplemented()
    {
        var handler = new FakeDell { FirstPwm = 19 };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(19, Percent(result, "1A").Value);
        Assert.Equal(37, Percent(result, "1B").Value);
        AssertOwnedLogout(handler);
    }

    [Fact]
    public async Task Supplemental_failure_preserves_valid_normal_percentage_including_zero()
    {
        var handler = new FakeDell
        {
            FirstSpeedPercent = 48, SecondSpeedPercent = 0,
            Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Path == "/cgi-bin/exec" ? new(HttpStatusCode.ServiceUnavailable) : null)
        };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(48, Percent(result, "1A").Value);
        Assert.Equal(0, Percent(result, "1B").Value);
        Assert.Equal("Available", Percent(result, "1A").Availability);
        Assert.Equal("Available", Percent(result, "1B").Availability);
        Assert.DoesNotContain(result.Metrics, value => value.MetricCode == "fan.pwm.percent");
        AssertOwnedLogout(handler);
    }

    [Theory]
    [InlineData("/cgi-bin/login")]
    [InlineData("/cgi-bin/exec")]
    public async Task Unsupported_supplement_cannot_replace_known_missing_normal_percentage(string unsupportedPath)
    {
        var handler = new FakeDell
        {
            DeclareFirstSpeedPercent = true,
            Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Path == unsupportedPath ? new(HttpStatusCode.NotFound) : null)
        };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1A").Availability);
        Assert.Equal("Unsupported", Percent(result, "1B").Availability);
    }

    [Fact]
    public async Task Valid_supplemental_pwm_has_priority_over_normal_percentage_in_the_same_unified_metric()
    {
        var handler = new FakeDell { FirstSpeedPercent = 48, SecondSpeedPercent = 50 };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(24, Percent(result, "1A").Value);
        Assert.Equal(37, Percent(result, "1B").Value);
        Assert.Equal(2, result.Metrics.Count(value => value.MetricCode == "fan.percent"));
        Assert.DoesNotContain(result.Metrics, value => value.MetricCode == "fan.pwm.percent");
        AssertOwnedLogout(handler);
    }

    [Fact]
    public async Task Valid_native_pwm_boundaries_skip_supplement_even_when_normal_percentage_differs()
    {
        var handler = new FakeDell { FirstPwm = 0, SecondPwm = 100, FirstSpeedPercent = 48, SecondSpeedPercent = 50 };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(0, Percent(result, "1A").Value);
        Assert.Equal(100, Percent(result, "1B").Value);
        Assert.DoesNotContain(handler.Requests, value => value.Path.StartsWith("/cgi-bin/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_native_pwm_can_be_supplemented_without_discarding_other_valid_native_pwm()
    {
        var handler = new FakeDell { FirstPwm = 101, FirstSpeedPercent = 48, SecondPwm = 19 };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(24, Percent(result, "1A").Value);
        Assert.Equal(19, Percent(result, "1B").Value);
        AssertOwnedLogout(handler);
    }

    [Theory]
    [InlineData("NA")]
    [InlineData("-1%")]
    [InlineData("101%")]
    [InlineData("NaN%")]
    [InlineData("24")]
    public async Task Invalid_pwm_is_persistable_missing_data_instead_of_zero_or_rpm_conversion(string pwm)
    {
        var handler = new FakeDell { SensorOutput = FanTable($"System Board Fan1A              Ok          5280RPM     600RPM      NA          {pwm}") };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1A").Availability);
        Assert.Equal(5280, Rpm(result, "1A").Value);
    }

    [Theory]
    [InlineData("0%", 0d)]
    [InlineData("100%", 100d)]
    public async Task Valid_pwm_boundaries_are_device_values(string pwm, double expected)
    {
        var handler = new FakeDell { SensorOutput = FanTable($"System Board Fan1A              Ok          5280RPM     600RPM      NA          {pwm}") };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(expected, Percent(result, "1A").Value);
        Assert.Equal("Available", Percent(result, "1A").Availability);
    }

    [Fact]
    public async Task Duplicate_and_similar_names_never_supply_another_physical_fans_pwm()
    {
        var handler = new FakeDell { SensorOutput = FanTable("""
            System Board Fan1A              Ok          5280RPM     600RPM      NA          24%
            System Board Fan1A              Ok          5280RPM     600RPM      NA          31%
            System Board Fan10B             Ok          4920RPM     600RPM      NA          37%
            """) };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Null(Percent(result, "1B").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1A").Availability);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1B").Availability);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(19d)]
    public async Task Duplicate_redfish_names_cannot_copy_one_racadm_value_to_two_physical_keys(double? existingFirstPwm)
    {
        // 即使同名风扇之一已有有效 Redfish PWM，缺测风扇仍不能借用该名称的 RACADM 读数。
        var handler = new FakeDell { FirstPwm = existingFirstPwm, SecondName = "System Board Fan1A" };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(existingFirstPwm, Percent(result, "1A").Value);
        Assert.Equal(existingFirstPwm is null ? "TemporarilyUnavailable" : "Available", Percent(result, "1A").Availability);
        Assert.Null(Percent(result, "1B").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1B").Availability);
        Assert.Equal(5280, Rpm(result, "1A").Value);
        Assert.Equal(4920, Rpm(result, "1B").Value);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData(401, "TemporarilyUnavailable")]
    [InlineData(403, "TemporarilyUnavailable")]
    [InlineData(404, "Unsupported")]
    public async Task Login_http_rejections_are_not_retried_and_do_not_discard_redfish(int status, string availability)
    {
        var handler = new FakeDell
        {
            Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Path == "/cgi-bin/login" ? new((HttpStatusCode)status) : null)
        };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Equal(5280, Rpm(result, "1A").Value);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal(availability, Percent(result, "1A").Availability);
        Assert.Single(handler.Requests, value => value.Path == "/cgi-bin/login");
        Assert.DoesNotContain(handler.Requests, value => value.Path is "/cgi-bin/exec" or "/cgi-bin/logout");
        Assert.All(result.Diagnostics, value => Assert.Null(value.Cause));
    }

    [Theory]
    [InlineData(401, "TemporarilyUnavailable")]
    [InlineData(403, "TemporarilyUnavailable")]
    [InlineData(404, "Unsupported")]
    public async Task Exec_http_rejections_are_not_retried_and_owned_session_is_closed(int status, string availability)
    {
        var handler = new FakeDell
        {
            Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Path == "/cgi-bin/exec" ? new((HttpStatusCode)status) : null)
        };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal(availability, Percent(result, "1A").Availability);
        Assert.Equal(4920, Rpm(result, "1B").Value);
        Assert.Single(handler.Requests, value => value.Path == "/cgi-bin/exec");
        AssertOwnedLogout(handler);
        AssertSafeRequests(handler);
    }

    [Theory]
    [InlineData("<LOGIN><RESP><RC>0x0</RC><SID>owned-sid</SID>")]
    [InlineData("<!DOCTYPE LOGIN [<!ENTITY payload 'owned-sid'>]><LOGIN><RESP><RC>0x0</RC><SID>&payload;</SID></RESP></LOGIN>")]
    public async Task Invalid_or_dtd_login_xml_cannot_create_a_trusted_session(string xml)
    {
        var handler = new FakeDell { LoginXml = xml };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1A").Availability);
        Assert.DoesNotContain(handler.Requests, value => value.Path is "/cgi-bin/exec" or "/cgi-bin/logout");
        Assert.Equal(5280, Rpm(result, "1A").Value);
    }

    [Theory]
    [InlineData("<EXEC><RESP><RC>0x0</RC><CMDRC>0x0</CMDRC><CMDOUTPUT>")]
    [InlineData("<!DOCTYPE EXEC [<!ENTITY payload 'not-trusted'>]><EXEC><RESP><RC>0x0</RC><CMDRC>0x0</CMDRC><CMDOUTPUT>&payload;</CMDOUTPUT></RESP></EXEC>")]
    public async Task Invalid_or_dtd_exec_xml_does_not_emit_pwm_and_session_is_closed(string xml)
    {
        var handler = new FakeDell { ExecXml = xml };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1A").Availability);
        AssertOwnedLogout(handler);
    }

    [Theory]
    [InlineData("<LOGIN><RESP><RC>0x1</RC><SID>owned-sid</SID></RESP></LOGIN>", false)]
    [InlineData("<EXEC><RESP><RC>0x1</RC><CMDRC>0x0</CMDRC><CMDOUTPUT>ignored</CMDOUTPUT></RESP></EXEC>", true)]
    [InlineData("<EXEC><RESP><RC>0x0</RC><CMDRC>0x1</CMDRC><CMDOUTPUT>ignored</CMDOUTPUT></RESP></EXEC>", true)]
    public async Task Nonzero_protocol_or_command_return_codes_do_not_become_measurements(string xml, bool exec)
    {
        var handler = new FakeDell { LoginXml = exec ? FakeDell.SuccessfulLogin : xml, ExecXml = exec ? xml : null };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1A").Availability);
        Assert.Equal(5280, Rpm(result, "1A").Value);
        if (exec) AssertOwnedLogout(handler);
        else Assert.DoesNotContain(handler.Requests, value => value.Path == "/cgi-bin/exec");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_io_faults_keep_real_exception_diagnostics_and_successful_metrics(bool timeout)
    {
        Exception cause = timeout ? new TaskCanceledException("fixture timeout") : new IOException("fixture transport fault");
        var handler = new FakeDell
        {
            Override = (request, _) => request.Path == "/cgi-bin/exec"
                ? Task.FromException<HttpResponseMessage?>(cause) : Task.FromResult<HttpResponseMessage?>(null)
        };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Contains(result.Diagnostics, value => ReferenceEquals(value.Cause, cause) && value.Category == "DeviceFailure");
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1A").Availability);
        Assert.Equal(120, Assert.Single(result.Metrics, value => value.MetricCode == "power.watts").Value);
        AssertOwnedLogout(handler);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_but_owned_session_cleanup_uses_an_independent_token()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeDell
        {
            Override = (request, token) =>
            {
                if (request.Path == "/cgi-bin/exec")
                {
                    cancellation.Cancel();
                    return Task.FromException<HttpResponseMessage?>(new OperationCanceledException(cancellation.Token));
                }
                if (request.Path == "/cgi-bin/logout") Assert.False(token.IsCancellationRequested);
                return Task.FromResult<HttpResponseMessage?>(null);
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Driver(handler).ReadTelemetryAsync(Connection, cancellation.Token));
        AssertOwnedLogout(handler);
        Assert.Single(handler.Requests, value => value.Path == "/cgi-bin/exec");
    }

    [Fact]
    public async Task Logout_failure_is_recorded_without_losing_pwm_or_closing_unowned_sessions()
    {
        var recorder = new ExceptionRecorder();
        var handler = new FakeDell
        {
            Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Path == "/cgi-bin/logout" ? new(HttpStatusCode.ServiceUnavailable) : null)
        };
        var result = await Driver(handler, recorder).ReadTelemetryAsync(Connection);
        Assert.Equal(24, Percent(result, "1A").Value);
        Assert.NotEmpty(recorder.Records);
        AssertOwnedLogout(handler);
    }

    [Fact]
    public async Task Redirects_are_not_followed_and_credentials_never_leave_the_device_origin()
    {
        var handler = new FakeDell
        {
            Override = (request, _) => Task.FromResult<HttpResponseMessage?>(request.Path == "/cgi-bin/login"
                ? new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.test/cgi-bin/login") } } : null)
        };
        var result = await Driver(handler).ReadTelemetryAsync(Connection);
        Assert.Null(Percent(result, "1A").Value);
        Assert.Equal("TemporarilyUnavailable", Percent(result, "1A").Availability);
        AssertSafeRequests(handler);
        Assert.Single(handler.Requests, value => value.Path == "/cgi-bin/login");
    }

    private static Idrac8Service Driver(FakeDell handler, IExceptionRecorder? recorder = null) => new(_ => new HttpClient(handler, disposeHandler: false), recorder);
    private static string FanTable(string rows) => "Sensor Type : FAN\r\n<Sensor Name>                   <Status>    <Reading>   <lc>        <uc>        <PWM %>\r\n" + rows + "\r\nSensor Type : VOLTAGE\r\n";
    private static MetricReading Percent(TelemetryReading result, string fan) => Assert.Single(result.Metrics, value => value.MetricCode == "fan.percent" && value.SensorKey == $"fan:0x17||Fan.Embedded.{fan}");
    private static MetricReading Rpm(TelemetryReading result, string fan) => Assert.Single(result.Metrics, value => value.MetricCode == "fan.rpm" && value.SensorKey == $"fan:0x17||Fan.Embedded.{fan}");

    private static void AssertOwnedLogout(FakeDell handler)
    {
        var logout = Assert.Single(handler.Requests, value => value.Path == "/cgi-bin/logout");
        Assert.Equal(HttpMethod.Get, logout.Method);
        Assert.Contains("sid=owned-sid", logout.Cookie ?? "");
    }

    private static void AssertSafeRequests(FakeDell handler)
    {
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("bmc.test", request.Host);
            Assert.DoesNotContain("Actions/", request.Path, StringComparison.OrdinalIgnoreCase);
            if (request.Method != HttpMethod.Get) Assert.Contains(request.Path, new[] { "/cgi-bin/login", "/cgi-bin/exec" });
        });
    }

    private sealed class ExceptionRecorder : IExceptionRecorder
    {
        public List<ExceptionLogContext> Records { get; } = [];
        public Task RecordAsync(Exception? exception, ExceptionLogContext context) { Records.Add(context); return Task.CompletedTask; }
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Host, string Body, string? Cookie);

    private sealed class FakeDell : HttpMessageHandler
    {
        public const string SuccessfulLogin = "<LOGIN><RESP><RC>0x0</RC><SID>owned-sid</SID></RESP></LOGIN>";
        public List<RequestRecord> Requests { get; } = [];
        public double? FirstPwm { get; init; }
        public double? SecondPwm { get; init; }
        public double? FirstSpeedPercent { get; init; }
        public double? SecondSpeedPercent { get; init; }
        public bool DeclareFirstSpeedPercent { get; init; }
        public string? SecondName { get; init; }
        public string LoginXml { get; init; } = SuccessfulLogin;
        public string? ExecXml { get; init; }
        public string? SensorOutput { get; init; }
        public Func<RequestRecord, CancellationToken, Task<HttpResponseMessage?>>? Override { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var record = new RequestRecord(request.Method, request.RequestUri!.PathAndQuery, request.RequestUri.Host,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null);
            Requests.Add(record);
            if (Override is not null && await Override(record, cancellationToken) is { } overridden) return overridden;
            if (record.Path == "/cgi-bin/login") return Xml(LoginXml);
            if (record.Path == "/cgi-bin/exec") return Xml(ExecXml ?? SuccessOutput(SensorOutput));
            if (record.Path == "/cgi-bin/logout") return Xml("<LOGOUT><RESP><RC>0x0</RC></RESP></LOGOUT>");
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("测试禁止任何其他写请求。");
            return record.Path switch
            {
                "/redfish/v1/" => Json("""{"Systems":{"@odata.id":"/redfish/v1/Systems"},"Chassis":{"@odata.id":"/redfish/v1/Chassis"}}"""),
                "/redfish/v1/Systems" => Json("""{"Members":[{"@odata.id":"/redfish/v1/Systems/server-X"}]}"""),
                "/redfish/v1/Systems/server-X" => Json("""{"@odata.id":"/redfish/v1/Systems/server-X","Manufacturer":"Dell Inc.","Model":"PowerEdge R630","PowerState":"On","Links":{"Chassis":[{"@odata.id":"/redfish/v1/Chassis/chassis-X"}]}}"""),
                "/redfish/v1/Chassis/chassis-X" => Json("""{"Power":{"@odata.id":"/redfish/v1/Chassis/chassis-X/Power"},"Thermal":{"@odata.id":"/redfish/v1/Chassis/chassis-X/Thermal"}}"""),
                "/redfish/v1/Chassis/chassis-X/Power" => Json("""{"PowerControl":[{"PowerConsumedWatts":120}]}"""),
                "/redfish/v1/Chassis/chassis-X/Thermal" => Json(System.Text.Json.JsonSerializer.Serialize(new
                {
                    Temperatures = new[] { new { MemberId = "CPU1", Name = "CPU1 Temp", ReadingCelsius = 31 } },
                    Fans = new[] { Fan("1A", 5280, FirstPwm, speedPercent: FirstSpeedPercent, declareSpeedPercent: DeclareFirstSpeedPercent), Fan("1B", 4920, SecondPwm, SecondName, SecondSpeedPercent) }
                })),
                _ => new(HttpStatusCode.NotFound)
            };
        }

        private static object Fan(string suffix, int rpm, double? pwm, string? name = null, double? speedPercent = null, bool declareSpeedPercent = false)
        {
            var fan = new Dictionary<string, object?>
            {
                ["MemberId"] = $"0x17||Fan.Embedded.{suffix}", ["Name"] = name ?? $"System Board Fan{suffix}", ["Reading"] = rpm, ["ReadingUnits"] = "RPM"
            };
            if (pwm is not null) fan["PwmPercent"] = pwm;
            if (speedPercent is not null || declareSpeedPercent) fan["SpeedPercent"] = speedPercent;
            return fan;
        }

        private static string SuccessOutput(string? output) => new XElement("EXEC", new XElement("RESP", new XElement("RC", "0x0"), new XElement("CMDRC", "0x0"),
            new XElement("CMDOUTPUT", output ?? """
                Sensor Type : FAN
                <Sensor Name>                   <Status>    <Reading>   <lc>        <uc>        <PWM %>
                System Board Fan1B              Ok          4920RPM     600RPM      NA          37%
                System Board Fan1A              Ok          5280RPM     600RPM      NA          24%
                Sensor Type : VOLTAGE
                """))).ToString(SaveOptions.DisableFormatting);

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Xml(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/xml") };
    }
}

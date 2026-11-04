using System.Net;
using System.Text;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Service.impl.OutOfBand;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>认证生命周期只进入内存 HTTP 处理器；这些测试绝不连接带外设备。</summary>
public sealed class RedfishAuthenticationTests
{
    private static readonly OutOfBandConnection Connection = new()
    {
        Endpoint = new Uri("https://bmc.test/"), Username = "test-user", Password = "test-secret"
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operation_reuses_token_without_basic_and_deletes_only_its_owned_session(bool discoverService)
    {
        var handler = new SessionBmc { DiscoverService = discoverService };
        var result = await Driver(handler).ReadInventoryAsync(Connection);
        Assert.Equal("PowerEdge R630", result.Identity.Model);
        Assert.Equal(1, handler.Logins);
        var owned = Assert.Single(handler.Requests, request => request.Method == "DELETE");
        Assert.Equal(SessionBmc.Sessions + "/owned-1", owned.Path);
        Assert.Equal("token-1", owned.Token);
        Assert.Null(owned.Authorization);
        var authenticated = handler.Requests.Where(request => request.Method == "GET" && request.Path.StartsWith("/redfish/v1/Systems", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(authenticated);
        Assert.All(authenticated, request => { Assert.Equal("token-1", request.Token); Assert.Null(request.Authorization); });
        Assert.All(handler.Requests, request => Assert.Equal("bmc.test", request.Host));
        using var login = JsonDocument.Parse(Assert.Single(handler.Requests, request => request.Method == "POST").Body!);
        Assert.Equal("test-user", login.RootElement.GetProperty("UserName").GetString());
        Assert.Equal("test-secret", login.RootElement.GetProperty("Password").GetString());
        Assert.All(result.Warnings, warning => { Assert.DoesNotContain("test-secret", warning); Assert.DoesNotContain("token-1", warning); });
    }

    [Fact]
    public async Task Explicit_get_only_mode_does_not_create_authentication_sessions()
    {
        var handler = new SessionBmc();
        var result = await Driver(handler).TestConnectionAsync(Connection with { UseSessionAuthentication = false });
        Assert.True(result.Success);
        Assert.All(handler.Requests, request => { Assert.Equal("GET", request.Method); Assert.Equal("Basic", request.Authorization); Assert.Null(request.Token); });
        Assert.Equal(0, handler.Logins);
    }

    [Fact]
    public async Task Ilo4_keeps_basic_compatibility_reads_even_when_session_option_is_true()
    {
        var handler = new SessionBmc();
        var driver = new Ilo4Service(_ => new HttpClient(handler));
        var result = await driver.ReadInventoryAsync(Connection with { UseSessionAuthentication = true });
        Assert.Equal("iLO4", result.Identity.ControllerType);
        Assert.Equal(0, handler.Logins);
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, request => { Assert.Equal("GET", request.Method); Assert.Equal("Basic", request.Authorization); Assert.Null(request.Token); });
    }

    [Fact]
    public async Task Expired_read_token_refreshes_once_and_cleans_up_both_owned_sessions()
    {
        var handler = new SessionBmc { ReadUnauthorized = request => request.Path == "/redfish/v1/Systems" && request.Token == "token-1" };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.True(result.Success);
        Assert.Equal(2, handler.Logins);
        Assert.Equal(2, handler.Requests.Count(request => request.Path == "/redfish/v1/Systems"));
        Assert.Equal(new[] { SessionBmc.Sessions + "/owned-1", SessionBmc.Sessions + "/owned-2" },
            handler.Requests.Where(request => request.Method == "DELETE").Select(request => request.Path));
    }

    [Fact]
    public async Task Repeated_read_unauthorized_does_not_loop_or_create_a_third_session()
    {
        var handler = new SessionBmc { ReadUnauthorized = request => request.Path == "/redfish/v1/Systems" };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal(2, handler.Logins);
        Assert.Equal(2, handler.Requests.Count(request => request.Path == "/redfish/v1/Systems"));
        Assert.Equal(2, handler.Requests.Count(request => request.Method == "DELETE"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Authentication_login_rejection_is_not_retried(int status)
    {
        var handler = new SessionBmc { LoginStatus = (HttpStatusCode)status };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal(status == 401 ? "AuthenticationFailed" : "PermissionDenied", result.ErrorCode);
        Assert.Equal(1, handler.Logins);
        Assert.DoesNotContain(handler.Requests, request => request.Method == "DELETE");
        Assert.DoesNotContain("test-secret", result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task Authentication_login_timeout_is_not_retried_or_guessed_for_cleanup()
    {
        var handler = new SessionBmc { LoginTimeout = true };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.False(result.Success);
        Assert.Equal("Timeout", result.ErrorCode);
        Assert.Equal(1, handler.Logins);
        Assert.DoesNotContain(handler.Requests, request => request.Method == "DELETE");
        Assert.DoesNotContain("test-secret", result.ErrorMessage ?? "");
    }

    [Fact]
    public async Task Unsupported_standard_sessions_endpoint_falls_back_to_basic_reads_once()
    {
        var handler = new SessionBmc { LoginStatus = HttpStatusCode.MethodNotAllowed };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.True(result.Success);
        Assert.Equal(1, handler.Logins);
        Assert.All(handler.Requests.Where(request => request.Method == "GET"), request => Assert.Equal("Basic", request.Authorization));
        Assert.Contains(result.Warnings, warning => warning.Contains("未支持此认证会话", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://other.test/redfish/v1/Sessions/foreign")]
    [InlineData("/redfish/v1/Managers/1/Actions/Manager.Reset")]
    [InlineData("/redfish/v1/SessionService/Sessions/foreign?secret=token-1")]
    [InlineData("/redfish/v1/SessionService/Sessions/nested%2Fforeign")]
    public async Task Unverifiable_session_location_is_not_reused_or_deleted(string? location)
    {
        var handler = new SessionBmc { Location = location };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.True(result.Success);
        Assert.Equal(1, handler.Logins);
        Assert.DoesNotContain(handler.Requests, request => request.Method == "DELETE");
        Assert.All(handler.Requests.Where(request => request.Method == "GET"), request => Assert.Null(request.Token));
        Assert.Contains(result.Warnings, warning => warning.Contains("未定位的会话", StringComparison.Ordinal));
        Assert.All(result.Warnings, warning => Assert.DoesNotContain("token-1", warning));
    }

    [Fact]
    public async Task Misleading_authentication_link_never_posts_to_device_action()
    {
        var handler = new SessionBmc { SessionsLink = "/redfish/v1/Systems/node/Actions/ComputerSystem.Reset" };
        var result = await Driver(handler).TestConnectionAsync(Connection);
        Assert.True(result.Success);
        Assert.Equal(0, handler.Logins);
        Assert.All(handler.Requests, request => Assert.Equal("GET", request.Method));
        Assert.Contains(result.Warnings, warning => warning.Contains("路径核验", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Power_unauthorized_is_never_resent_or_refreshed()
    {
        var handler = new SessionBmc { PowerStatus = HttpStatusCode.Unauthorized };
        var result = await Driver(handler).ExecutePowerActionAsync(Connection, PowerAction.ForceRestart);
        Assert.Equal("Rejected", result.Status);
        Assert.Equal("AuthenticationFailed", result.ErrorCode);
        Assert.Equal(1, handler.Logins);
        Assert.Single(handler.Requests, request => request.Path.EndsWith("ComputerSystem.Reset", StringComparison.Ordinal));
        Assert.Single(handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task Session_cleanup_failure_is_visible_without_hiding_inventory()
    {
        var handler = new SessionBmc { CleanupStatus = HttpStatusCode.Unauthorized };
        var result = await Driver(handler).ReadInventoryAsync(Connection);
        Assert.Equal("PowerEdge R630", result.Identity.Model);
        Assert.Contains(result.Warnings, warning => warning.Contains("未确认注销", StringComparison.Ordinal));
        Assert.Single(handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task Cleanup_native_fault_is_added_to_the_returned_inventory_after_disposal()
    {
        var cause = new HttpRequestException("fixture-secret cleanup failure");
        var handler = new SessionBmc { CleanupException = cause };
        var result = await Driver(handler).ReadInventoryAsync(Connection);
        Assert.Equal("PowerEdge R630", result.Identity.Model);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "SessionCleanupFailed");
        Assert.Equal("DeviceFailure", diagnostic.Category);
        Assert.Same(cause, diagnostic.Cause);
        Assert.Contains(result.Warnings, warning => warning.Contains("注销未完成", StringComparison.Ordinal));
        Assert.Single(handler.Requests, request => request.Method == "DELETE");
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Cancellation_still_cleans_up_owned_session_with_independent_deadline()
    {
        var handler = new SessionBmc { CancelOnSystems = true };
        using var cancellation = new CancellationTokenSource();
        handler.Cancel = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Driver(handler).ReadInventoryAsync(Connection, cancellation.Token));
        Assert.Single(handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task Cancellation_with_cleanup_fault_records_original_cause_even_without_returned_result()
    {
        var cause = new HttpRequestException("fixture-secret cleanup failure");
        var recorder = new RecordingExceptions();
        var handler = new SessionBmc { CancelOnSystems = true, CleanupException = cause };
        using var cancellation = new CancellationTokenSource();
        handler.Cancel = cancellation.Cancel;
        var driver = new Idrac8Service(_ => new HttpClient(handler), recorder);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ReadInventoryAsync(Connection, cancellation.Token));
        var recorded = Assert.Single(recorder.Records);
        Assert.Same(cause, recorded.Cause);
        Assert.Equal("DeviceFailure", recorded.Context.Kind);
        Assert.Equal("DisposeSession", recorded.Context.Operation);
        Assert.Equal("SessionCleanupFailed", recorded.Context.ErrorCode);
        Assert.DoesNotContain("fixture-secret", recorded.Context.Message);
        Assert.Null(recorded.Context.ServerId);
        Assert.Single(handler.Requests, request => request.Method == "DELETE");
    }

    [Fact]
    public async Task Cleanup_recording_fault_cannot_change_successful_device_result_or_resend()
    {
        var handler = new SessionBmc { CleanupException = new HttpRequestException("fixture-secret") };
        var recorder = new RecordingExceptions { Fail = true };
        var result = await new Idrac8Service(_ => new HttpClient(handler), recorder).ReadInventoryAsync(Connection);
        Assert.Equal("PowerEdge R630", result.Identity.Model);
        Assert.Same(handler.CleanupException, Assert.Single(result.Diagnostics).Cause);
        Assert.Single(recorder.Records);
        Assert.Single(handler.Requests, request => request.Method == "DELETE");
    }

    private sealed class RecordingExceptions : IExceptionRecorder
    {
        public List<(Exception? Cause, ExceptionLogContext Context)> Records { get; } = [];
        public bool Fail { get; init; }
        public Task RecordAsync(Exception? exception, ExceptionLogContext context)
        {
            Records.Add((exception, context));
            return Fail ? Task.FromException(new IOException("fixture-recorder-failure")) : Task.CompletedTask;
        }
    }

    private static Idrac8Service Driver(SessionBmc handler) => new(_ => new HttpClient(handler));

    private sealed record RecordedRequest(string Method, string Path, string Host, string? Authorization, string? Token, string? Body);

    /// <summary>专用模拟器把认证 POST 与电源 POST 分开计数，避免重试断言被认证请求干扰。</summary>
    private sealed class SessionBmc : HttpMessageHandler
    {
        public const string Sessions = "/redfish/v1/SessionService/Sessions";
        public List<RecordedRequest> Requests { get; } = [];
        public bool DiscoverService { get; init; }
        public string SessionsLink { get; init; } = Sessions;
        public string? Location { get; init; } = "owned";
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.Created;
        public HttpStatusCode CleanupStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode PowerStatus { get; init; } = HttpStatusCode.NoContent;
        public Func<RecordedRequest, bool>? ReadUnauthorized { get; init; }
        public bool CancelOnSystems { get; init; }
        public bool LoginTimeout { get; init; }
        public Exception? CleanupException { get; init; }
        public Action? Cancel { get; set; }
        public int Logins { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Host,
                request.Headers.Authorization?.Scheme, request.Headers.TryGetValues("X-Auth-Token", out var values) ? values.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(recorded);
            if (request.Method == HttpMethod.Post && recorded.Path == Sessions)
            {
                Logins++;
                if (LoginTimeout) throw new OperationCanceledException("test-secret must never escape");
                var response = new HttpResponseMessage(LoginStatus);
                if (response.IsSuccessStatusCode)
                {
                    response.Headers.TryAddWithoutValidation("X-Auth-Token", "token-" + Logins);
                    if (Location is not null) response.Headers.Location = new Uri(Location == "owned" ? Sessions + "/owned-" + Logins : Location, UriKind.RelativeOrAbsolute);
                }
                return response;
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (CleanupException is not null) throw CleanupException;
                return new HttpResponseMessage(CleanupStatus);
            }
            if (request.Method == HttpMethod.Post && recorded.Path.EndsWith("ComputerSystem.Reset", StringComparison.Ordinal)) return new HttpResponseMessage(PowerStatus);
            if (CancelOnSystems && recorded.Path == "/redfish/v1/Systems")
            { Cancel?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); }
            if (ReadUnauthorized?.Invoke(recorded) == true) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var json = recorded.Path switch
            {
                "/redfish/v1/" => JsonSerializer.Serialize(DiscoverService
                    ? new Dictionary<string, object> { ["Systems"] = Link("/redfish/v1/Systems"), ["SessionService"] = Link("/redfish/v1/SessionService") }
                    : new Dictionary<string, object> { ["Systems"] = Link("/redfish/v1/Systems"), ["Sessions"] = Link(SessionsLink) }),
                "/redfish/v1/SessionService" => JsonSerializer.Serialize(new Dictionary<string, object> { ["Sessions"] = Link(SessionsLink) }),
                "/redfish/v1/Systems" => """{"Members":[{"@odata.id":"/redfish/v1/Systems/node"}]}""",
                "/redfish/v1/Systems/node" => """{"@odata.id":"/redfish/v1/Systems/node","Manufacturer":"Dell","Model":"PowerEdge R630","PowerState":"On","Actions":{"#ComputerSystem.Reset":{"target":"/redfish/v1/Systems/node/Actions/ComputerSystem.Reset","ResetType@Redfish.AllowableValues":["ForceRestart"]}}}""",
                _ => "{}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static object Link(string path) => new Dictionary<string, string> { ["@odata.id"] = path };
    }
}

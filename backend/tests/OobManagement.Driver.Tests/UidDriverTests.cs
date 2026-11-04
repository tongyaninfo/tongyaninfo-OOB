using System.Net;
using System.Text;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Common;
using OobManagement.Infrastructure.Service.impl.OutOfBand;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>UID 的读、写、拒绝及未知结果都由内存 HTTP 处理器验证，不访问真实服务器。</summary>
public sealed class UidDriverTests
{
    private static readonly OutOfBandConnection Connection = new()
    { Endpoint = new Uri("https://bmc.test/"), Username = "fixture-user", Password = "fixture-secret", UseSessionAuthentication = false };

    [Theory]
    [InlineData(true, UidState.Lit, UidControlState.Lit, UidState.Blinking)]
    [InlineData(false, UidState.Blinking, UidControlState.Blinking, UidState.Lit)]
    public async Task Controller_uid_profile_exposes_and_executes_only_the_verified_on_mode(bool hp, UidState onState, UidControlState onControl, UidState unsupported)
    {
        var handler = new FakeUid();
        var driver = Driver(handler, hp);
        var current = await driver.ReadUidAsync(Connection);
        Assert.Equal(new[] { UidControlState.Off, onControl }, current.Status.SupportedStates);
        var result = await driver.SetUidAsync(Connection, onState);
        Assert.True(result.Success);
        Assert.Equal(onState, result.Status.State);
        Assert.Equal(1, handler.PatchCount);
        var refused = await driver.SetUidAsync(Connection, unsupported);
        Assert.Equal("UID_UNSUPPORTED", refused.ErrorCode);
        Assert.Equal(1, handler.PatchCount);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Explicit_device_allowable_values_restrict_but_never_expand_the_verified_profile(bool hp, bool allowOn)
    {
        var handler = new FakeUid { AllowedValues = allowOn ? ["Off", "Lit", "Blinking"] : ["Off"] };
        var target = hp ? UidState.Lit : UidState.Blinking;
        var driver = Driver(handler, hp);
        var read = await driver.ReadUidAsync(Connection);
        Assert.Equal(allowOn ? new[] { UidControlState.Off, hp ? UidControlState.Lit : UidControlState.Blinking } : [UidControlState.Off], read.Status.SupportedStates);
        var result = await driver.SetUidAsync(Connection, target);
        Assert.Equal(allowOn, result.Success);
        if (!allowOn) Assert.Equal("UID_UNSUPPORTED", result.ErrorCode);
        Assert.Equal(allowOn ? 1 : 0, handler.PatchCount);
    }

    [Theory]
    [InlineData(false, "Off", UidState.Off)]
    [InlineData(true, "Lit", UidState.Lit)]
    [InlineData(true, "Blinking", UidState.Blinking)]
    public async Task Read_uid_is_get_only_and_reports_the_verified_controller_profile(bool hp, string state, UidState expected)
    {
        var handler = new FakeUid { State = state };
        var result = await Driver(handler, hp).ReadUidAsync(Connection);
        Assert.True(result.Success);
        Assert.Equal(expected, result.Status.State);
        Assert.True(result.Status.CanControl);
        Assert.Equal(new[] { UidControlState.Off, hp ? UidControlState.Lit : UidControlState.Blinking }, result.Status.SupportedStates);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.All(handler.Requests, request => Assert.NotEqual(true, request.ConnectionClose));
        Assert.Equal(0, handler.PatchCount);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("UnsupportedFirmwareValue", true)]
    [InlineData("Off", false)]
    public async Task Missing_field_unknown_value_or_missing_patch_capability_cannot_trigger_a_write(string? state, bool allowPatch)
    {
        var handler = new FakeUid { State = state, AllowPatch = allowPatch };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking);
        Assert.Equal("UID_UNSUPPORTED", result.ErrorCode);
        Assert.False(result.Status.CanControl);
        Assert.Equal(0, handler.PatchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_uid_patch_targets_discovered_system_once_and_verifies_fresh_device_state(bool hp)
    {
        var handler = new FakeUid();
        var target = hp ? UidState.Lit : UidState.Blinking;
        var result = await Driver(handler, hp).SetUidAsync(Connection, target);
        Assert.True(result.Success);
        Assert.Equal(target, result.Status.State);
        var patch = Assert.Single(handler.Requests, request => request.Method == HttpMethod.Patch);
        Assert.Equal(FakeUid.SystemPath, patch.Path);
        Assert.Equal(JsonSerializer.Serialize(new { IndicatorLED = target.ToString() }), patch.Body);
        Assert.Equal("4.0", patch.ODataVersion);
        Assert.Equal(2, handler.SystemReadCount);
        Assert.All(handler.Requests, request => Assert.Equal("bmc.test", request.Host));
        Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("Actions", StringComparison.OrdinalIgnoreCase));
        Assert.All(handler.Requests, request => Assert.Equal(hp ? true : (bool?)null, request.ConnectionClose));
    }

    [Fact]
    public async Task Ilo_uid_write_closes_every_discovery_patch_and_readback_connection_before_the_next_request()
    {
        var handler = new FakeUid { FailReusedPatch = true };
        var result = await Driver(handler, hp: true).SetUidAsync(Connection, UidState.Lit);
        Assert.True(result.Success);
        Assert.Equal(UidState.Lit, result.Status.State);
        Assert.Equal(1, handler.PatchCount);
        Assert.Equal(2, handler.SystemReadCount);
        Assert.All(handler.Requests, request => Assert.True(request.ConnectionClose));
    }

    [Theory]
    [InlineData("MessageId")]
    [InlineData("MessageID")]
    public async Task Ilo_explicit_success_error_envelope_requires_a_fresh_readback_before_success(string messageIdProperty)
    {
        var body = "{\"error\":{\"code\":\"iLO.0.10.ExtendedInfo\",\"@Message.ExtendedInfo\":[{\"" + messageIdProperty + "\":\"Base.0.10.Success\"}]}}";
        var handler = new FakeUid { Override = (request, device, _) =>
        {
            if (request.Method != HttpMethod.Patch) return Task.FromResult<HttpResponseMessage?>(null);
            device.State = "Lit";
            return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK) { Content = new StringContent(body) });
        } };
        var result = await Driver(handler, hp: true).SetUidAsync(Connection, UidState.Lit);
        Assert.True(result.Success);
        Assert.Equal(UidState.Lit, result.Status.State);
        Assert.Equal(1, handler.PatchCount);
        Assert.Equal(2, handler.SystemReadCount);
    }

    [Fact]
    public async Task Ilo_success_message_without_observed_target_state_is_still_unknown()
    {
        var handler = new FakeUid { Override = (request, _, _) => Task.FromResult<HttpResponseMessage?>(request.Method == HttpMethod.Patch
            ? new(HttpStatusCode.OK) { Content = new StringContent("""{"error":{"code":"iLO.0.10.ExtendedInfo","@Message.ExtendedInfo":[{"MessageId":"Base.0.10.Success"}]}}""") } : null) };
        var result = await Driver(handler, hp: true).SetUidAsync(Connection, UidState.Lit);
        Assert.Equal("UID_RESULT_UNKNOWN", result.ErrorCode);
        Assert.Equal(UidState.Unknown, result.Status.State);
        Assert.Equal(1, handler.PatchCount);
        Assert.Equal(2, handler.SystemReadCount);
    }

    [Theory]
    [InlineData("""{"error":{"code":"iLO.0.10.ExtendedInfo","@Message.ExtendedInfo":[{"MessageId":"Base.0.10.Success"},{"MessageId":"Base.0.10.PropertyValueError"}]}}""")]
    [InlineData("""{"error":{"@Message.ExtendedInfo":[{"MessageId":"Base.0.10.Success"}]},"Messages":[{"MessageID":"Base.0.10.PropertyValueError"}]}""")]
    [InlineData("""{"error":{"@Message.ExtendedInfo":[{"MessageId":"Base.0.10.Success"},{}]}}""")]
    [InlineData("""{"error":{"@Message.ExtendedInfo":[]}}""")]
    [InlineData("""{"error":{"@Message.ExtendedInfo":[{"MessageId":"Vendor.0.10.Success"}]}}""")]
    [InlineData("""{"error":{"@Message.ExtendedInfo":[{"MessageId":"Base.0.10.NotSuccess"}]}}""")]
    [InlineData("""{"error":{"@Message.ExtendedInfo":[{"MessageId":"Base.0.10.Success","Severity":"Critical"}]}}""")]
    [InlineData("""{"error":{"code":"Base.0.10.GeneralError","@Message.ExtendedInfo":[{"MessageId":"Base.0.10.Success"}]}}""")]
    [InlineData("""{"error":{"message":"Undeclared outcome"},"Messages":[{"MessageID":"Base.0.10.Success"}]}""")]
    public async Task Ilo_mixed_unknown_empty_or_conflicting_messages_cannot_hide_a_rejected_patch(string body)
    {
        var handler = new FakeUid { Override = (request, _, _) => Task.FromResult<HttpResponseMessage?>(request.Method == HttpMethod.Patch
            ? new(HttpStatusCode.OK) { Content = new StringContent(body) } : null) };
        var result = await Driver(handler, hp: true).SetUidAsync(Connection, UidState.Lit);
        Assert.Equal("UID_COMMAND_REJECTED", result.ErrorCode);
        Assert.Equal(1, handler.PatchCount);
        Assert.Equal(1, handler.SystemReadCount);
    }

    [Fact]
    public async Task Ilo_success_error_envelope_compatibility_does_not_change_other_controller_responses()
    {
        var handler = new FakeUid { Override = (request, _, _) => Task.FromResult<HttpResponseMessage?>(request.Method == HttpMethod.Patch
            ? new(HttpStatusCode.OK) { Content = new StringContent("""{"error":{"@Message.ExtendedInfo":[{"MessageId":"Base.0.10.Success"}]}}""") } : null) };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking);
        Assert.Equal("UID_COMMAND_REJECTED", result.ErrorCode);
        Assert.Equal(1, handler.PatchCount);
        Assert.Equal(1, handler.SystemReadCount);
    }

    [Theory]
    [InlineData(401, "UID_AUTHENTICATION_FAILED", 401)]
    [InlineData(403, "UID_PERMISSION_DENIED", 403)]
    [InlineData(404, "UID_UNSUPPORTED", 422)]
    [InlineData(405, "UID_UNSUPPORTED", 422)]
    [InlineData(501, "UID_UNSUPPORTED", 422)]
    [InlineData(429, "UID_RATE_LIMITED", 429)]
    [InlineData(500, "UID_RESULT_UNKNOWN", 502)]
    [InlineData(408, "UID_RESULT_UNKNOWN", 502)]
    public async Task Patch_rejection_and_unknown_http_statuses_are_distinct_and_never_retried(int status, string code, int apiStatus)
    {
        var handler = new FakeUid { PatchStatus = (HttpStatusCode)status };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking);
        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(apiStatus, result.StatusCode);
        Assert.Equal(1, handler.PatchCount);
        Assert.Equal(1, handler.SystemReadCount);
        if (code == "UID_RESULT_UNKNOWN") Assert.Equal(UidState.Unknown, result.Status.State);
        if (code == "UID_UNSUPPORTED")
        {
            Assert.Equal(UidState.Off, result.Status.State);
            Assert.False(result.Status.CanControl);
            Assert.Empty(result.Status.SupportedStates);
        }
    }

    [Fact]
    public async Task Accepted_patch_without_observed_target_state_is_unknown_instead_of_false_success()
    {
        var handler = new FakeUid { ApplyPatch = false };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking);
        Assert.Equal("UID_RESULT_UNKNOWN", result.ErrorCode);
        Assert.Equal(UidState.Unknown, result.Status.State);
        Assert.False(result.Status.CanControl);
        Assert.Equal(1, handler.PatchCount);
        Assert.Equal(2, handler.SystemReadCount);
    }

    [Fact]
    public async Task Failure_reading_back_an_accepted_patch_remains_unknown_and_retains_native_diagnostic()
    {
        var failure = new IOException("fixture-secret must stay in diagnostics");
        var handler = new FakeUid { Override = (request, device, _) => request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == FakeUid.SystemPath && device.PatchCount > 0
            ? Task.FromException<HttpResponseMessage?>(failure) : Task.FromResult<HttpResponseMessage?>(null) };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking);
        Assert.Equal("UID_RESULT_UNKNOWN", result.ErrorCode);
        Assert.Equal(UidState.Unknown, result.Status.State);
        Assert.Contains(result.Diagnostics, diagnostic => ReferenceEquals(failure, diagnostic.Cause));
        Assert.DoesNotContain("fixture-secret", JsonStorage.Write(result));
        Assert.Equal(1, handler.PatchCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Broken_patch_response_is_unknown_and_native_exception_is_not_exposed(bool hp)
    {
        var failure = new IOException("fixture-secret-write-response");
        var handler = new FakeUid { Override = (request, _, _) => request.Method == HttpMethod.Patch
            ? Task.FromException<HttpResponseMessage?>(failure) : Task.FromResult<HttpResponseMessage?>(null) };
        var result = await Driver(handler, hp).SetUidAsync(Connection, hp ? UidState.Lit : UidState.Blinking);
        Assert.Equal("UID_RESULT_UNKNOWN", result.ErrorCode);
        Assert.Equal(UidState.Unknown, result.Status.State);
        Assert.Contains(result.Diagnostics, diagnostic => ReferenceEquals(failure, diagnostic.Cause));
        Assert.DoesNotContain("fixture-secret", JsonStorage.Write(result));
        Assert.Equal(1, handler.PatchCount);
    }

    [Theory]
    [InlineData("fixture-secret-invalid-json", "UID_RESULT_UNKNOWN")]
    [InlineData("{\"error\":{\"code\":\"Rejected\"}}", "UID_COMMAND_REJECTED")]
    public async Task Invalid_or_explicit_error_patch_body_cannot_be_success(string body, string code)
    {
        var handler = new FakeUid { Override = (request, _, _) => Task.FromResult<HttpResponseMessage?>(request.Method == HttpMethod.Patch
            ? new(HttpStatusCode.OK) { Content = new StringContent(body) } : null) };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(1, handler.PatchCount);
        Assert.Equal(1, handler.SystemReadCount);
        Assert.DoesNotContain("fixture-secret", JsonStorage.Write(result));
    }

    [Fact]
    public async Task Patch_redirect_is_not_followed_and_no_credentials_leave_the_origin()
    {
        var handler = new FakeUid { Override = (request, _, _) => Task.FromResult<HttpResponseMessage?>(request.Method == HttpMethod.Patch
            ? new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://other.test/redfish/v1/Systems/other") } } : null) };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking);
        Assert.Equal("UID_COMMAND_REJECTED", result.ErrorCode);
        Assert.Equal(1, handler.PatchCount);
        Assert.All(handler.Requests, request => Assert.Equal("bmc.test", request.Host));
    }

    [Fact]
    public async Task Cancellation_before_patch_is_not_sent_and_cannot_be_reported_as_unknown_execution()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeUid { Override = (request, _, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == FakeUid.SystemPath)
            { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            return Task.FromResult<HttpResponseMessage?>(null);
        } };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking, cancellation.Token);
        Assert.Equal("UID_NOT_SENT", result.ErrorCode);
        Assert.Equal(0, handler.PatchCount);
    }

    [Fact]
    public async Task Cancellation_after_patch_stays_unknown_without_a_second_write()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeUid { Override = (request, _, _) =>
        {
            if (request.Method == HttpMethod.Patch) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            return Task.FromResult<HttpResponseMessage?>(null);
        } };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking, cancellation.Token);
        Assert.Equal("UID_RESULT_UNKNOWN", result.ErrorCode);
        Assert.Equal(UidState.Unknown, result.Status.State);
        Assert.Equal(1, handler.PatchCount);
    }

    [Theory]
    [InlineData("https://other.test/redfish/v1/Systems/other")]
    [InlineData("/redfish/v1/Systems/uid-Z/Actions/ComputerSystem.Reset")]
    [InlineData("/redfish/v1/Systems/uid-Z?configuration=true")]
    public async Task Untrusted_or_action_resource_identity_cannot_be_used_as_a_uid_target(string identity)
    {
        var handler = new FakeUid { IdentityLink = identity };
        var result = await Driver(handler).SetUidAsync(Connection, UidState.Blinking);
        Assert.False(result.Success);
        Assert.Equal(0, handler.PatchCount);
        Assert.All(handler.Requests, request => Assert.Equal("bmc.test", request.Host));
    }

    [Theory]
    [InlineData(UidState.Unknown)]
    [InlineData((UidState)99)]
    public async Task Unsupported_write_states_are_rejected_before_any_device_request(UidState state)
    {
        var handler = new FakeUid();
        var result = await Driver(handler).SetUidAsync(Connection, state);
        Assert.Equal("UID_INVALID_STATE", result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    private static IOutOfBandService Driver(FakeUid handler, bool hp = false) => hp
        ? new Ilo4Service(_ => new HttpClient(handler, disposeHandler: false)) : new Idrac8Service(_ => new HttpClient(handler, disposeHandler: false));

    private sealed record RequestRecord(HttpMethod Method, string Path, string Host, string Body, string? ODataVersion, bool? ConnectionClose);

    private sealed class FakeUid : HttpMessageHandler
    {
        public const string SystemPath = "/redfish/v1/Systems/uid-Z";
        public List<RequestRecord> Requests { get; } = [];
        public string? State { get; set; } = "Off";
        public string[]? AllowedValues { get; init; }
        public bool AllowPatch { get; init; } = true;
        public bool ApplyPatch { get; init; } = true;
        public bool FailReusedPatch { get; init; }
        private bool _reusableGetConnection;
        public HttpStatusCode PatchStatus { get; init; } = HttpStatusCode.NoContent;
        public string IdentityLink { get; init; } = SystemPath;
        public int PatchCount { get; private set; }
        public int SystemReadCount { get; private set; }
        public Func<HttpRequestMessage, FakeUid, CancellationToken, Task<HttpResponseMessage?>>? Override { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Host, body,
                request.Headers.TryGetValues("OData-Version", out var versions) ? versions.Single() : null, request.Headers.ConnectionClose));
            if (request.Method == HttpMethod.Patch) PatchCount++;
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == SystemPath) SystemReadCount++;
            // 复现 iLO4 的 GET 连接回池后 PATCH 响应头中断；只给 PATCH 加 close 仍会复用前一条连接。
            if (request.Method == HttpMethod.Patch && FailReusedPatch && _reusableGetConnection)
                throw new HttpRequestException(HttpRequestError.ResponseEnded, "Fixture connection ended before UID response headers.");
            if (request.Method == HttpMethod.Get) _reusableGetConnection = request.Headers.ConnectionClose != true;
            if (Override is not null && await Override(request, this, cancellationToken) is { } overridden) return overridden;
            if (request.Method == HttpMethod.Patch)
            {
                if (PatchStatus == HttpStatusCode.NoContent && ApplyPatch) State = JsonDocument.Parse(body).RootElement.GetProperty("IndicatorLED").GetString();
                return new(PatchStatus);
            }
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("UID 夹具拒绝电源或其他写操作。");
            return request.RequestUri.AbsolutePath switch
            {
                "/redfish/v1/" => Json("""{"Systems":{"@odata.id":"/redfish/v1/Systems"}}"""),
                "/redfish/v1/Systems" => Json("""{"Members":[{"@odata.id":"/redfish/v1/Systems/uid-Z"}]}"""),
                SystemPath => SystemResource(),
                _ => new(HttpStatusCode.NotFound)
            };
        }

        private HttpResponseMessage SystemResource()
        {
            var value = new Dictionary<string, object?> { ["@odata.id"] = IdentityLink, ["Manufacturer"] = "Fixture vendor", ["Model"] = "Fixture model" };
            if (State is not null) value["IndicatorLED"] = State;
            if (AllowedValues is not null) value["IndicatorLED@Redfish.AllowableValues"] = AllowedValues;
            var response = Json(JsonSerializer.Serialize(value));
            response.Content.Headers.Allow.Add("GET");
            if (AllowPatch) response.Content.Headers.Allow.Add("PATCH");
            return response;
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

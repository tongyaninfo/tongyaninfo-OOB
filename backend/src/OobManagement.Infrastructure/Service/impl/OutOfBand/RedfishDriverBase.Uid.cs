using System.Text.Json;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

public abstract partial class RedfishDriverBase
{
    public async Task<UidDeviceResult> ReadUidAsync(OutOfBandConnection connection, CancellationToken cancellationToken = default)
    {
        var created = CreateTransport(connection);
        if (!created.Success) return UidFailure(created.Failure!, []);
        await using var transport = created.Data;
        RedfishResult<Resources> discovered;
        try { discovered = await DiscoverAsync(transport, cancellationToken); }
        catch (OperationCanceledException error)
        { return UidFailure(new("UID_READ_FAILED", "UID 状态读取已取消或超时。", cancellationToken.IsCancellationRequested ? "Business" : "DeviceFailure", error), transport.Diagnostics); }
        if (!discovered.Success) return UidFailure(discovered.Failure!, transport.Diagnostics);
        return new() { Status = UidStatus(transport, discovered.Data.System), Diagnostics = transport.Diagnostics };
    }

    public async Task<UidDeviceResult> SetUidAsync(OutOfBandConnection connection, UidState state, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeBeforeSend = null)
    {
        UidControlState? requested = state switch
        {
            UidState.Off => UidControlState.Off, UidState.Lit => UidControlState.Lit,
            UidState.Blinking => UidControlState.Blinking, _ => null
        };
        if (requested is null) return UidFailure(new("UID_INVALID_STATE", "UID 只允许关灯、常亮或闪烁，具体以设备能力为准。"), []);
        var created = CreateTransport(connection, closeCommandConnections: ControllerType == "iLO4");
        if (!created.Success) return UidFailure(created.Failure!, []);
        await using var transport = created.Data;
        RedfishResult<Resources> discovered;
        try { discovered = await DiscoverAsync(transport, cancellationToken); }
        catch (OperationCanceledException error)
        { return UidFailure(new("UID_NOT_SENT", "UID 请求发送前取消或超时，未发送设备修改。", cancellationToken.IsCancellationRequested ? "Business" : "DeviceFailure", error), transport.Diagnostics); }
        if (!discovered.Success) return UidFailure(discovered.Failure!, transport.Diagnostics);
        var current = UidStatus(transport, discovered.Data.System);
        if (!current.CanControl || !current.SupportedStates.Contains(requested.Value))
            return new() { Status = current, ErrorCode = "UID_UNSUPPORTED", ErrorMessage = "设备未声明支持所选 UID 状态。", StatusCode = 422, Diagnostics = transport.Diagnostics };
        var systemLink = JsonValue.Link(discovered.Data.System)!;
        if (authorizeBeforeSend is not null)
        {
            ServiceResult<bool> authorization;
            try { authorization = await authorizeBeforeSend(cancellationToken); }
            catch (OperationCanceledException error)
            { return UidFailure(new("UID_NOT_SENT", "权限核验在发送前取消，UID 命令未发送。", "Business", error), transport.Diagnostics, current); }
            catch (Exception error)
            {
                return new() { Status = current, ErrorCode = "AUTHORIZATION_UNAVAILABLE", ErrorMessage = "发送前权限核验暂不可用，UID 命令未发送。", StatusCode = 503,
                    Diagnostics = [.. transport.Diagnostics, new("AUTHORIZATION_UNAVAILABLE", "发送前权限核验暂不可用，UID 命令未发送。", "SystemFailure", error)] };
            }
            if (!authorization.Success || !authorization.Data)
                return new() { Status = current with { CanControl = false, SupportedStates = [] }, ErrorCode = authorization.ErrorCode ?? "FORBIDDEN",
                    ErrorMessage = authorization.ErrorMessage ?? "没有服务器管理权限，UID 命令未发送。", StatusCode = authorization.Success ? 403 : authorization.StatusCode,
                    Diagnostics = transport.Diagnostics };
        }
        var sent = await transport.SendUidAsync(systemLink, state, cancellationToken);
        if (!sent.Success) return UidFailure(sent.Failure!, transport.Diagnostics,
            sent.Failure!.Code == "UID_RESULT_UNKNOWN" ? null : current);
        try
        {
            var checkedSystem = await transport.ReadFreshUidAsync(systemLink, cancellationToken);
            if (checkedSystem.Success)
            {
                var checkedStatus = UidStatus(transport, checkedSystem.Data);
                if (checkedStatus.State == state) return new() { Status = checkedStatus, Diagnostics = transport.Diagnostics };
            }
            return UidFailure(new("UID_RESULT_UNKNOWN", "UID 修改已发出，但尚未核对到目标状态；请查询状态，勿自动重发。", "DeviceFailure"), transport.Diagnostics);
        }
        catch (OperationCanceledException error)
        { return UidFailure(new("UID_RESULT_UNKNOWN", "UID 请求已发出，核对状态时取消或超时；请查询当前状态。", cancellationToken.IsCancellationRequested ? "Business" : "DeviceFailure", error), transport.Diagnostics); }
    }

    private UidStatusDto UidStatus(RedfishTransport transport, JsonElement system)
    {
        var text = JsonValue.Text(system, "IndicatorLED");
        var state = text switch { "Off" => UidState.Off, "Lit" => UidState.Lit, "Blinking" => UidState.Blinking, _ => UidState.Unknown };
        var link = JsonValue.Link(system);
        var canWrite = state != UidState.Unknown && link is not null && transport.SupportsUidPatch(link);
        // 两种控制器的定位模式不同：采用已实测的能力，不把常亮自动替换为闪烁重发。
        UidControlState[] states = !canWrite ? [] : ControllerType switch
        {
            "iLO4" => [UidControlState.Off, UidControlState.Lit],
            "iDRAC8" => [UidControlState.Off, UidControlState.Blinking],
            _ => []
        };
        if (canWrite && JsonValue.Has(system, "IndicatorLED@Redfish.AllowableValues"))
        {
            var allowed = JsonValue.Array(system, "IndicatorLED@Redfish.AllowableValues").Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).ToArray();
            states = states.Where(value => allowed.Contains(value.ToString(), StringComparer.Ordinal)).ToArray();
        }
        return new(state, states.Length > 0, states, DateTimeOffset.UtcNow,
            state == UidState.Unknown ? "设备未提供可识别的 UID 指示灯状态。" : states.Length == 0 ? "当前 UID 状态可读，但设备未声明支持修改。" : null);
    }

    private static UidDeviceResult UidFailure(DeviceDiagnostic failure, IReadOnlyList<DeviceDiagnostic> diagnostics, UidStatusDto? current = null)
    {
        var code = failure.Code.StartsWith("UID_", StringComparison.Ordinal) ? failure.Code : "UID_READ_FAILED";
        var status = code switch
        {
            "UID_INVALID_STATE" => 400, "UID_NOT_SENT" => 409, "UID_UNSUPPORTED" => 422, "UID_AUTHENTICATION_FAILED" => 401,
            "UID_PERMISSION_DENIED" => 403, "UID_RATE_LIMITED" => 429, "UID_COMMAND_REJECTED" => 409, _ => 502
        };
        if (code == "UID_UNSUPPORTED" && current is not null)
            current = current with { CanControl = false, SupportedStates = [], Message = failure.SafeMessage };
        return new()
        {
            Status = current ?? new(UidState.Unknown, false, [], DateTimeOffset.UtcNow, failure.SafeMessage),
            ErrorCode = code, ErrorMessage = failure.SafeMessage, StatusCode = status,
            Diagnostics = diagnostics.Contains(failure) ? diagnostics : [.. diagnostics, failure]
        };
    }
}

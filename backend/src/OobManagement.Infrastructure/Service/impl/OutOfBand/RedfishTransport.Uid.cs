using System.Net;
using System.Text;
using System.Text.Json;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>UID 只允许修改本轮实际发现的主机资源 IndicatorLED，不开放任意 PATCH。</summary>
internal sealed partial class RedfishTransport
{
    private readonly HashSet<string> _uidPatchResources = new(StringComparer.Ordinal);

    private void RememberUidMethods(Uri target, string originalKey, HttpResponseMessage response)
    {
        var methods = response.Content.Headers.Allow.Concat(response.Headers.TryGetValues("Allow", out var values) ? values : []);
        var patch = methods.SelectMany(value => value.Split(',')).Any(value => value.Trim().Equals("PATCH", StringComparison.OrdinalIgnoreCase));
        foreach (var key in new[] { target.AbsoluteUri.TrimEnd('/'), originalKey.TrimEnd('/') })
            if (patch) _uidPatchResources.Add(key); else _uidPatchResources.Remove(key);
    }

    public bool SupportsUidPatch(string systemLink) => ResolveUidTarget(systemLink) is { Success: true } target
        && _uidPatchResources.Contains(target.Data.AbsoluteUri.TrimEnd('/'));

    private RedfishResult<Uri> ResolveUidTarget(string systemLink)
    {
        var result = Resolve(systemLink);
        if (!result.Success) return result;
        var uri = result.Data;
        const string prefix = "/redfish/v1/Systems/";
        var path = uri.AbsolutePath;
        var member = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(path[prefix.Length..].TrimEnd('/')) : "";
        if (member.Length == 0 || member.Contains('/') || member.Contains('\\') || member is "." or ".." || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return Fail<Uri>("UID_UNSUPPORTED", "设备未提供可核验的主机 UID 资源入口。");
        return result;
    }

    public Task<RedfishResult<JsonElement>> ReadFreshUidAsync(string systemLink, CancellationToken cancellationToken)
    {
        // 写后必须重新读取设备，不能用本轮发现阶段的缓存冒充核对成功。
        _cache.Clear();
        return GetAsync(systemLink, cancellationToken);
    }

    public async Task<RedfishResult<bool>> SendUidAsync(string systemLink, UidState state, CancellationToken cancellationToken)
    {
        if (state is not (UidState.Off or UidState.Lit or UidState.Blinking)) return Fail<bool>("UID_INVALID_STATE", "UID 只支持明确关灯、常亮或闪烁。");
        var resolved = ResolveUidTarget(systemLink);
        if (!resolved.Success) return RedfishResult<bool>.Fail(resolved.Failure!);
        if (!SupportsUidPatch(systemLink)) return Fail<bool>("UID_UNSUPPORTED", "设备没有声明支持 UID 修改。");
        var sent = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = Request(HttpMethod.Patch, resolved.Data);
            request.Content = new StringContent(JsonSerializer.Serialize(new { IndicatorLED = state.ToString() }), Encoding.UTF8, "application/json");
            cancellationToken.ThrowIfCancellationRequested();
            sent = true;
            // 写请求只发送一次；认证拒绝、重定向、超时和结果未知均不得触发认证刷新或重发。
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 200 and < 300)
            {
                var bytes = await ReadBoundedAsync(response.Content, deadline.Token);
                if (!bytes.Success) return Fail<bool>("UID_RESULT_UNKNOWN", "UID 响应未能完整读取，请查询当前状态后再操作。", "DeviceFailure", bytes.Failure?.Cause);
                if (bytes.Data.Length > 0)
                {
                    using var document = JsonDocument.Parse(bytes.Data);
                    var root = document.RootElement;
                    if ((JsonValue.Has(root, "error") || _compatibilityRead && (JsonValue.Has(root, "Messages") || JsonValue.Has(root, "@Message.ExtendedInfo")))
                        && (!_compatibilityRead || !HasOnlyUidSuccessMessages(root)))
                        return Fail<bool>("UID_COMMAND_REJECTED", "设备返回错误，未接受 UID 修改。");
                }
                return RedfishResult<bool>.Ok(true);
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
                return Fail<bool>("UID_UNSUPPORTED", "设备不支持此 UID 修改接口。");
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                return Fail<bool>("UID_RESULT_UNKNOWN", "设备未明确确认 UID 修改结果，请查询当前状态后再操作。", "DeviceFailure");
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => Fail<bool>("UID_AUTHENTICATION_FAILED", "设备未接受 UID 操作认证。"),
                HttpStatusCode.Forbidden => Fail<bool>("UID_PERMISSION_DENIED", "设备拒绝 UID 修改权限。"),
                HttpStatusCode.TooManyRequests => Fail<bool>("UID_RATE_LIMITED", "设备请求过于频繁，本次 UID 修改被拒绝。"),
                _ => Fail<bool>("UID_COMMAND_REJECTED", "设备拒绝 UID 修改，未跟随重定向或重发请求。")
            };
        }
        catch (OperationCanceledException error) when (sent)
        { return Fail<bool>("UID_RESULT_UNKNOWN", "等待 UID 修改结果时超时或取消，请查询当前状态后再操作。", cancellationToken.IsCancellationRequested ? "Business" : "DeviceFailure", error); }
        catch (OperationCanceledException error)
        { return Fail<bool>("UID_NOT_SENT", "UID 请求发送前取消或超时，未发送设备修改。", cancellationToken.IsCancellationRequested ? "Business" : "DeviceFailure", error); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        { return Fail<bool>("UID_RESULT_UNKNOWN", "UID 请求发出后通信或响应解析中断，请查询当前状态后再操作。", "DeviceFailure", error); }
    }

    private static bool HasOnlyUidSuccessMessages(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var foundMessages = false;
        if (!ValidateUidMessages(root, ref foundMessages)) return false;
        foreach (var property in root.EnumerateObject().Where(item => item.Name.Equals("error", StringComparison.OrdinalIgnoreCase)))
        {
            var foundErrorMessages = false;
            if (property.Value.ValueKind != JsonValueKind.Object || !ValidateUidMessages(property.Value, ref foundErrorMessages) || !foundErrorMessages) return false;
            foundMessages = true;
            foreach (var code in property.Value.EnumerateObject().Where(item => item.Name.Equals("code", StringComparison.OrdinalIgnoreCase)))
            {
                if (code.Value.ValueKind != JsonValueKind.String) return false;
                var value = code.Value.GetString();
                if (!IsUidMessageId(value, "Base", "Success") && !IsUidMessageId(value, "iLO", "ExtendedInfo")) return false;
            }
        }
        return foundMessages;
    }

    private static bool ValidateUidMessages(JsonElement container, ref bool foundMessages)
    {
        foreach (var property in container.EnumerateObject().Where(item => item.Name.Equals("Messages", StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals("@Message.ExtendedInfo", StringComparison.OrdinalIgnoreCase)))
        {
            if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() == 0) return false;
            foreach (var message in property.Value.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object) return false;
                var foundId = false;
                foreach (var field in message.EnumerateObject())
                {
                    if (field.Name.Equals("MessageId", StringComparison.OrdinalIgnoreCase))
                    {
                        if (field.Value.ValueKind != JsonValueKind.String || !IsUidMessageId(field.Value.GetString(), "Base", "Success")) return false;
                        foundId = true;
                    }
                    else if (field.Name.Equals("Severity", StringComparison.OrdinalIgnoreCase)
                        && (field.Value.ValueKind != JsonValueKind.String || field.Value.GetString() != "OK")) return false;
                }
                if (!foundId) return false;
                foundMessages = true;
            }
        }
        // iLO4 会把明确成功也放进 error 容器；只接受所有消息均为标准 Success，之后仍必须回读 UID。
        return true;
    }

    private static bool IsUidMessageId(string? value, string registry, string key)
    {
        var parts = value?.Split('.');
        return parts is { Length: 4 } && parts[0] == registry && parts[3] == key
            && parts[1].Length > 0 && parts[1].All(char.IsAsciiDigit) && parts[2].Length > 0 && parts[2].All(char.IsAsciiDigit);
    }
}

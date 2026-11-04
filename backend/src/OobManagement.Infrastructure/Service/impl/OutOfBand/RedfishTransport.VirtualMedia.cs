using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

internal sealed partial class RedfishTransport
{
    /// <summary>只发送设备声明的介质动作一次；不允许通过此方法访问电源或任意 OEM 入口。</summary>
    public async Task<ServiceResult<string>> SendVirtualMediaAsync(VirtualMediaDeviceSlot slot, Uri? image, CancellationToken ct)
    {
        var resource = Resolve(slot.Id);
        var action = Resolve((image is null ? slot.EjectTarget : slot.InsertTarget) ?? "");
        if (!resource.Success || !action.Success || !Regex.IsMatch(resource.Data.AbsolutePath,
                "^/(redfish|rest)/v1/Managers/[^/]+/VirtualMedia/[^/]+/?$", RegexOptions.IgnoreCase))
            return ServiceResult<string>.Fail("MEDIA_ACTION_INVALID", "设备未声明可核验的虚拟介质动作", 409);
        var expected = resource.Data.AbsolutePath.TrimEnd('/') + (slot.UsesIloOem
            ? "/Actions/Oem/Hp/HpiLOVirtualMedia." + (image is null ? "EjectVirtualMedia" : "InsertVirtualMedia")
            : "/Actions/VirtualMedia." + (image is null ? "EjectMedia" : "InsertMedia"));
        if (!action.Data.AbsolutePath.TrimEnd('/').Equals(expected, StringComparison.OrdinalIgnoreCase)
            || action.Data.Query.Length != 0 || action.Data.Fragment.Length != 0)
            return ServiceResult<string>.Fail("MEDIA_ACTION_INVALID", "介质动作目标与设备资源不匹配", 409);
        if (image is not null && slot.UsesIloOem && !slot.WriteProtected)
            return ServiceResult<string>.Fail("MEDIA_READ_ONLY_UNSUPPORTED", "该设备的此类介质未确认支持只读挂载", 409);
        var authenticated = await EnsureAuthenticationAsync(ct);
        if (!authenticated.Success)
            return ServiceResult<string>.Fail("MEDIA_AUTHENTICATION_FAILED", authenticated.Failure!.SafeMessage, 502);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var sent = false;
        try
        {
            using var request = Request(HttpMethod.Post, action.Data);
            object body = image is null ? new { } : slot.UsesIloOem
                ? new { Image = image.AbsoluteUri }
                : new { Image = image.AbsoluteUri, Inserted = true, WriteProtected = true };
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            ct.ThrowIfCancellationRequested();
            sent = true;
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
                return ServiceResult<string>.Ok("Unknown"); // 写请求不跟随重定向，更不降级重发。
            if (!response.IsSuccessStatusCode)
                return (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout
                    ? ServiceResult<string>.Ok("Unknown")
                    : ServiceResult<string>.Fail("MEDIA_DEVICE_REJECTED", "设备拒绝介质操作，请检查权限、许可与介质占用", 409);
            if (response.Content.Headers.ContentLength == 0) return ServiceResult<string>.Ok("Submitted");
            var content = await ReadBoundedAsync(response.Content, deadline.Token);
            if (!content.Success) return ServiceResult<string>.Ok("Unknown");
            if (content.Data.Length == 0) return ServiceResult<string>.Ok("Submitted");
            using var parsed = JsonDocument.Parse(content.Data);
            // iLO 会以 error 对象承载 Base.Success；其他错误响应不能当成命令已接受。
            var error = JsonValue.Get(parsed.RootElement, "error");
            if (error is not null)
            {
                var messages = JsonValue.Array(error.Value, "@Message.ExtendedInfo");
                var success = messages.Any(x => JsonValue.Text(x, "MessageId")?.EndsWith(".Success", StringComparison.OrdinalIgnoreCase) == true)
                    || JsonValue.Text(error.Value, "code")?.EndsWith(".Success", StringComparison.OrdinalIgnoreCase) == true;
                if (!success) return ServiceResult<string>.Fail("MEDIA_DEVICE_REJECTED", "设备返回介质操作拒绝", 409);
            }
            return ServiceResult<string>.Ok("Submitted");
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or JsonException)
        {
            // 发送后中断与未知响应均保留原操作核对，不重发挂载、卸载或认证请求。
            return sent ? ServiceResult<string>.Ok("Unknown")
                : ServiceResult<string>.Fail("MEDIA_NOT_SENT", "介质操作尚未发送，请检查设备连接", 502);
        }
    }
}

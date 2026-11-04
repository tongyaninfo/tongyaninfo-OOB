using OobManagement.Application.Service;

namespace OobManagement.Api.Service.impl;

/// <summary>身份变更提交后关闭本实例已有实时连接，不向设备发电源或介质卸载命令。</summary>
public sealed class UserSessionRevoker(TelemetryConnections telemetry, IConsoleSessionService console,
    IExceptionRecorder exceptions) : IUserSessionRevoker
{
    public async Task RevokeAsync(long userId, CancellationToken ct)
    {
        // 两种通道分别处理，单个通道关闭故障不能阻止另一个通道撤销，更不能把已提交的改密伪装为未保存。
        try { telemetry.DisconnectUser(userId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception exception) { await RecordAsync(exception, userId, "Telemetry"); }
        try { await console.CloseUserAsync(userId, CancellationToken.None); }
        catch (Exception exception) { await RecordAsync(exception, userId, "Console"); }
    }

    private Task RecordAsync(Exception exception, long userId, string channel) => exceptions.RecordAsync(exception, new()
    {
        Source = "Authentication", Operation = "Revoke" + channel, UserId = userId,
        ErrorCode = "USER_SESSION_REVOKE_FAILED", Message = "账号身份变更已保存，但已有连接关闭失败，数据库安全版本继续阻止旧身份访问"
    });
}

namespace OobManagement.Application.Models;

/// <summary>只在服务内传递的解密凭据；不得作为 HTTP 响应、日志或持久化明文。</summary>
public sealed record MailTransportConfiguration(string Host, int Port, string SecurityMode,
    string FromAddress, string Username, string Password)
{
    public override string ToString() => nameof(MailTransportConfiguration);
}

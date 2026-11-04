namespace OobManagement.Domain.Models;

/// <summary>唯一 SMTP 配置；密码使用宿主密钥和邮件独立 purpose 加密，保存和查询不触发发信。</summary>
public sealed class MailConfiguration
{
    public long Id { get; set; }
    public int Slot { get; set; } = 1;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 465;
    public string SecurityMode { get; set; } = "SslOnConnect";
    public string FromAddress { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordCiphertext { get; set; } = "";
    public long Version { get; set; } = 1;
    public string ReportSchedulesJson { get; set; } = "[]";
    public DateTimeOffset UpdatedAt { get; set; }
}

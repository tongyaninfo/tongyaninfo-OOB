namespace OobManagement.Application.Models;

/// <summary>密码仅返回是否已配置，不把明文、密文或密钥信息带到浏览器。</summary>
public sealed record MailConfigurationDto(bool Configured, string Host, int Port, string SecurityMode,
    string FromAddress, string Username, bool PasswordConfigured, long Version, DateTimeOffset? UpdatedAt);

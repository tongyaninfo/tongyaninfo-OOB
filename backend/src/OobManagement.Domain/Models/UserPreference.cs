namespace OobManagement.Domain.Models;

/// <summary>账号设置只允许白名单外观和表格项，不承载权限或认证内容。</summary>
public sealed class UserPreference
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

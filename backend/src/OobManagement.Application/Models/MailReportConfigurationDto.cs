namespace OobManagement.Application.Models;

/// <summary>复用SMTP单例的乐观并发版本，避免报表计划和邮箱保存互相覆盖。</summary>
public sealed record MailReportConfigurationDto(bool Configured, long Version, IReadOnlyList<MailReportScheduleDto> Schedules);

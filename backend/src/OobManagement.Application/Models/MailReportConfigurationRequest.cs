namespace OobManagement.Application.Models;

/// <summary>每次提交完整的日、周、月计划；版本过期需重新读取。</summary>
public sealed record MailReportConfigurationRequest(long Version, IReadOnlyList<MailReportScheduleRequest> Schedules);

namespace OobManagement.Application.Models;

/// <summary>启用起点只由服务端生成，读取与保存计划不会发送邮件。</summary>
public sealed record MailReportScheduleDto(string Period, bool Enabled, string ToAddress,
    string SendTime, int DayOfWeek, int DayOfMonth, DateTimeOffset? EffectiveFrom, DateTimeOffset? NextSendAt);

namespace OobManagement.Application.Models;

/// <summary>固定三类计划；上海时间，周一为1，短月发送日期取当月最后一天。</summary>
public sealed record MailReportScheduleRequest(string Period, bool Enabled, string ToAddress,
    string SendTime, int DayOfWeek = 1, int DayOfMonth = 1);

namespace OobManagement.Domain.Models;

/// <summary>邮件配置JSON中的固定报表槽位；不是独立数据表，启用起点不接受客户端覆盖。</summary>
public sealed record MailReportPlan(string Period, bool Enabled, string ToAddress, string SendTime,
    int DayOfWeek, int DayOfMonth, DateTimeOffset? EffectiveFrom);

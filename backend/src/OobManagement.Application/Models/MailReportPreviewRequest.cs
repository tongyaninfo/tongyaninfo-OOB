namespace OobManagement.Application.Models;

/// <summary>UTC传输的左闭右开统计窗口，最多366天。</summary>
public sealed record MailReportPreviewRequest(DateTimeOffset From, DateTimeOffset To);

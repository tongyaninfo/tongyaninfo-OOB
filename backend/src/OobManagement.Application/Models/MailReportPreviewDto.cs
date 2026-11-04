namespace OobManagement.Application.Models;

/// <summary>只读生成的报表快照，可预览或下载，不创建发件意图。</summary>
public sealed record MailReportPreviewDto(string Subject, string TextBody, string HtmlBody,
    DateTimeOffset From, DateTimeOffset To);

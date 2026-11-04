using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>只读取已入库运行数据并生成邮件快照；预览不采集设备、不发送邮件。</summary>
public interface IOperationReportBuilder
{
    Task<ServiceResult<MailReportPreviewDto>> BuildAsync(DateTimeOffset from, DateTimeOffset to,
        string title, CancellationToken ct);
}

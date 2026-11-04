namespace OobManagement.Domain.Models;

/// <summary>记录采集的实际结果和耗时，不保存设备响应正文或认证头。</summary>
public sealed class PollRun
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public string Status { get; set; } = "";
    public int MetricCount { get; set; }
    public string? ErrorMessage { get; set; }
}

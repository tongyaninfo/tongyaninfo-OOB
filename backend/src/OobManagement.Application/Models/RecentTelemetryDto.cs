namespace OobManagement.Application.Models;

/// <summary>近期曲线使用实际采样时刻；失败轮次另传，避免固定时间桶制造假断点。</summary>
public sealed record RecentTelemetryDto(IReadOnlyList<HistorySeriesDto> Series, IReadOnlyList<CollectionGapDto> Gaps);

/// <summary>确实失败的采集区间；不把正常采样抖动或没有样本的聚合桶当成故障。</summary>
public sealed record CollectionGapDto(DateTimeOffset From, DateTimeOffset To);

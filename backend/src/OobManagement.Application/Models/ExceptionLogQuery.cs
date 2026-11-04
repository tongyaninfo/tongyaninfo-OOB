namespace OobManagement.Application.Models;

/// <summary>异常检索限制分页与筛选长度，避免管理员页面形成无界查询。</summary>
public sealed record ExceptionLogQuery
{
    public int Current { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? Kind { get; init; }
    public string? Level { get; init; }
    public string? Source { get; init; }
    public long? ServerId { get; init; }
    public string? Keyword { get; init; }
}

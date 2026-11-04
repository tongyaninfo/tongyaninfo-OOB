using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>按本系统发件记录进行服务端筛选、排序和分页，不查询远端邮箱。</summary>
public sealed record MailMessageQuery
{
    [MaxLength(100)] public string? Keyword { get; init; }
    public string? Status { get; init; }
    public string? Kind { get; init; }
    [Range(1, 1000000)] public int Current { get; init; } = 1;
    [Range(1, 100)] public int PageSize { get; init; } = 20;
    public string? SortField { get; init; }
    public string? SortOrder { get; init; }
}

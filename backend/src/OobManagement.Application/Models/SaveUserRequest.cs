using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>初始密码仅新增时提交；编辑通过独立重设入口改密，不允许绑定系统管理员兼容标记。</summary>
public sealed record SaveUserRequest
{
    [Required, MaxLength(100)] public string Username { get; init; } = "";
    [Required, MaxLength(100)] public string DisplayName { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool CanManageUsers { get; init; }
    public bool CanUseConsole { get; init; }
    public bool CanManageServers { get; init; }
    public bool CanOperatePower { get; init; }
    public string? Password { get; init; }
    public long? Version { get; init; }
}

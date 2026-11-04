namespace OobManagement.Application.Models;

/// <summary>用户管理视图不返回密码、哈希或会话版本，长整型按统一合同输出十进制字符串。</summary>
public sealed record UserDto(string Id, string Username, string DisplayName, bool Enabled,
    DateTimeOffset? DeletedAt, bool IsAdministrator, bool CanManageUsers, bool CanUseConsole,
    long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool CanManageServers = false, bool CanOperatePower = false);

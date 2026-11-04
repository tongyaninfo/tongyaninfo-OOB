namespace OobManagement.Domain.Models;

/// <summary>账户只保存不可逆密码哈希，不保存初始明文密码。</summary>
public sealed class OobUser
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    /// <summary>由数据库按账号计算，登录与唯一校验采用相同的大小写规范化规则。</summary>
    public string NormalizedUsername { get; private set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? DeletedAt { get; set; }
    /// <summary>保留系统管理员身份保护及异常管理，不通过普通用户管理接口赋予，也不绕过显式业务权限。</summary>
    public bool IsAdministrator { get; set; }
    public bool CanManageUsers { get; set; }
    public bool CanUseConsole { get; set; }
    public bool CanManageServers { get; set; }
    public bool CanOperatePower { get; set; }
    /// <summary>敏感身份变化递增，使已签发 Cookie 及控制台连接失效。</summary>
    public long AuthVersion { get; set; } = 1;
    /// <summary>所有编辑递增，拒绝覆盖其他管理员已经保存的修改。</summary>
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

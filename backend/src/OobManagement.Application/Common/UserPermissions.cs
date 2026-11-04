namespace OobManagement.Application.Common;

/// <summary>四项业务权限独立于既有系统管理员角色，管理员也不能绕过明确撤销的权限。</summary>
public static class UserPermissions
{
    public const string UserManagement = "UserManagement";
    public const string VirtualConsole = "VirtualConsole";
    public const string ServerManagement = "ServerManagement";
    public const string PowerOperations = "PowerOperations";
    public const string PermissionClaim = "oob:permission";
    public const string AuthVersionClaim = "oob:auth-version";
}

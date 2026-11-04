namespace OobManagement.Application.Models;

/// <summary>仅由已校验的登录身份构造，敏感服务仍按版本重新核验数据库权限。</summary>
public readonly record struct UserSessionContext(long UserId, long AuthVersion);

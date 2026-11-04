namespace OobManagement.Application.Models;

/// <summary>宿主签发身份使用的内部认证结果，安全版本不放入用户展示接口。</summary>
public sealed record AuthenticatedUser(CurrentUserDto User, long AuthVersion);

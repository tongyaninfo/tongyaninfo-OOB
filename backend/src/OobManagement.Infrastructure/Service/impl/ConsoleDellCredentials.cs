namespace OobManagement.Infrastructure.Service.impl;

/// <summary>单会话临时认证材料；禁止序列化或写入日志，媒体凭据只由已认证 KVM 响应更新。</summary>
internal sealed class ConsoleDellCredentials(string user, string password)
{
    private readonly object _gate = new();
    private string _user = user;
    private string _password = password;
    private string? _mediaUser;
    private string? _mediaPassword;
    public (string User, string Password) Get(bool media)
    {
        lock (_gate)
            return media && _mediaUser is not null && _mediaPassword is not null
                ? (_mediaUser, _mediaPassword) : (_user, _password);
    }
    public void SetMedia(string user, string password)
    { lock (_gate) { _mediaUser = user; _mediaPassword = password; } }
    public void Clear()
    { lock (_gate) { _user = _password = ""; _mediaUser = _mediaPassword = null; } }
}

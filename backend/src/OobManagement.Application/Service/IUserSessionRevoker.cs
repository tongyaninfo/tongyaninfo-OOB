namespace OobManagement.Application.Service;

/// <summary>身份事务提交后撤销本实例连接，不触发设备电源或独立虚拟介质卸载。</summary>
public interface IUserSessionRevoker
{
    Task RevokeAsync(long userId, CancellationToken ct);
}

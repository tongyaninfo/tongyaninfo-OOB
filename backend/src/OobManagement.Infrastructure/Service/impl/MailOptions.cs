namespace OobManagement.Infrastructure.Service.impl;

/// <summary>由宿主 Mail 配置节提供受信任网络策略，页面不能自行放开连接目标。</summary>
public sealed class MailOptions
{
    public string[] AllowedPrivateHosts { get; set; } = [];
    public int[] AllowedPorts { get; set; } = [25, 465, 587];
}

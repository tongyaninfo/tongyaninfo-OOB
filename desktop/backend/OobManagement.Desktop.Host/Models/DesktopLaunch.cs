namespace OobManagement.Desktop.Host.Models;

/// <summary>本次父子进程握手；秘密只通过标准输入进入内存，不写配置或 URL。</summary>
public sealed record DesktopLaunch(string Command, string LaunchSecret, string InstanceId);

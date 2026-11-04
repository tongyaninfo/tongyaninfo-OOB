namespace OobManagement.Application.Models;

/// <summary>仅后端使用的已登记目标，浏览器不能指定任意代理主机或端口。</summary>
public sealed record ConsoleTarget(long ServerId, string ServerName, string ControllerType,
    string? FirmwareVersion, OutOfBandConnection Connection, long AuthVersion = 1);

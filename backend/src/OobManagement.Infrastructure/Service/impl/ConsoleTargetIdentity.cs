using System.Security.Cryptography;
using System.Text;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>服务器记录可改管理地址；以规范化网络目标和控制器类型绑定会话、介质意图，避免跨设备认领。</summary>
public static class ConsoleTargetIdentity
{
    public static string Create(ConsoleTarget target)
    {
        var endpoint = target.Connection.Endpoint;
        var normalized = $"{target.ControllerType.Trim().ToUpperInvariant()}|{endpoint.Scheme.ToLowerInvariant()}|{endpoint.IdnHost.ToLowerInvariant()}|{endpoint.Port}|{endpoint.AbsolutePath.TrimEnd('/')}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}

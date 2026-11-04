using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace OobManagement.Api.Service.impl;

/// <summary>登记本实例的只读连接，退出登录时中止该账号已有连接，防止 Cookie 退出后继续收流。</summary>
public sealed class TelemetryConnections
{
    private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();

    public void Register(HubCallerContext connection) => _connections[connection.ConnectionId] = connection;
    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);
    /// <summary>事件只面向产生时已连接的订阅者；重连会生成新 ID，不接收队列中断线期间的旧事件。</summary>
    public string[] CaptureConnectionIds() => _connections.Keys.ToArray();

    public void DisconnectUser(string userId)
    {
        foreach (var pair in _connections.Where(pair => pair.Value.UserIdentifier == userId))
            if (_connections.TryRemove(pair.Key, out var connection)) connection.Abort();
    }
}

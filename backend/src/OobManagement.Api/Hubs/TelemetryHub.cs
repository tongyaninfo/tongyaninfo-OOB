using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OobManagement.Api.Service.impl;
using System.Security.Claims;
using OobManagement.Application.Service;

namespace OobManagement.Api.Hubs;

/// <summary>向有效登录账号推送已入库快照；连接、重连不补发历史，也不提供设备操作方法。</summary>
[Authorize]
public sealed class TelemetryHub(TelemetryConnections connections, IAuthenticationService authentication) : Hub
{
    public override async Task OnConnectedAsync()
    {
        connections.Register(Context);
        // 先登记再复核，避免 HTTP 已认证但撤销发生于 WebSocket 登记前时漏掉关闭。
        if (!long.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !long.TryParse(Context.User?.FindFirstValue("oob:auth-version"), out var version)
            || await authentication.ValidateSessionAsync(userId, version, Context.ConnectionAborted) is null)
        {
            connections.Remove(Context.ConnectionId);
            Context.Abort();
            return;
        }
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

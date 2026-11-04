using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>客户端重试复用同一请求标识，避免额外占用控制器会话。</summary>
public sealed record ConsoleSessionRequest([Required, MaxLength(100)] string RequestId);

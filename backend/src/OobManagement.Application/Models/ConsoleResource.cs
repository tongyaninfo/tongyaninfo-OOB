namespace OobManagement.Application.Models;

/// <summary>已由厂商适配器检查大小、路径和内容类型的控制台资源。</summary>
public sealed record ConsoleResource(byte[] Content, string ContentType, int StatusCode = 200, string? ScriptNonce = null);

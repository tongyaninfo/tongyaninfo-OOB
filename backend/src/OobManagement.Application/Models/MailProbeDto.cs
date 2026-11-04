namespace OobManagement.Application.Models;

/// <summary>连接测试只检查连接与认证，不发送邮件，也不创建发件意图。</summary>
public sealed record MailProbeDto(bool Connected, string? ErrorCode, string Message);

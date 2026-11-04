using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>初次只需主机、发件邮箱和密码；高级项使用安全默认值，编辑密码为空保留旧值。</summary>
public sealed record MailConfigurationRequest
{
    [Required, MaxLength(253)] public string Host { get; init; } = "";
    [Required, MaxLength(320)] public string FromAddress { get; init; } = "";
    [MaxLength(512)] public string? Password { get; init; }
    [MaxLength(320)] public string? Username { get; init; }
    [Range(1, 65535)] public int Port { get; init; } = 465;
    [Required, MaxLength(32)] public string SecurityMode { get; init; } = "SslOnConnect";
    [Range(0, long.MaxValue)] public long Version { get; init; }
    public override string ToString() => "MailConfigurationRequest { Password = [REDACTED] }";
}

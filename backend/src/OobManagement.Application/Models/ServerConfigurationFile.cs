using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>可跨宿主迁移的认证加密包，二进制字段均为 Base64，不包含明文设备配置。</summary>
public sealed record ServerConfigurationFile(
    [Required, MaxLength(64)] string Format,
    int Version,
    [Required, MaxLength(24)] string Salt,
    [Required, MaxLength(16)] string Nonce,
    [Required, MaxLength(2796204)] string Ciphertext,
    [Required, MaxLength(24)] string Tag);

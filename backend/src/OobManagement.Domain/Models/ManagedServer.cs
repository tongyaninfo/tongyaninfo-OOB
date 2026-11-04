namespace OobManagement.Domain.Models;

/// <summary>服务器连接密码以可恢复密文保存，仅采集服务可解密。</summary>
public sealed class ManagedServer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Brand { get; set; } = "";
    public string ControllerType { get; set; } = "";
    public string Address { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordCiphertext { get; set; } = "";
    public string? CertificateFingerprint { get; set; }
    /// <summary>仅放宽当前设备证书验证；显式证书指纹仍优先严格匹配。</summary>
    public bool IgnoreCertificateErrors { get; set; } = true;
    public int SamplingIntervalSeconds { get; set; } = 30;
    public bool Enabled { get; set; } = true;
    public string IdentityJson { get; set; } = "{}";
    public string CapabilitiesJson { get; set; } = "{}";
    public string LatestTelemetryJson { get; set; } = "{}";
    public string ConnectionStatus { get; set; } = "Pending";
    public DateTimeOffset? LastCollectedAt { get; set; }
    public DateTimeOffset? LastInventoryAt { get; set; }
    public DateTimeOffset? NextPollAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

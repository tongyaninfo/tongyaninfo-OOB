using System.Security.Cryptography.X509Certificates;

namespace OobManagement.Api.Common;

/// <summary>宿主静态配置不存入页面授权记录，证书只读取明确配置的现有文件。</summary>
public sealed class McpHostOptions
{
    public bool Supported { get; set; } = true;
    public string PublicBaseUrl { get; set; } = "";
    public string SigningCertificatePath { get; set; } = "";
    public string EncryptionCertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    public string[] KnownProxies { get; set; } = [];
    public X509Certificate2? SigningCertificate { get; private set; }
    public X509Certificate2? EncryptionCertificate { get; private set; }
    public string? CertificateError { get; private set; }
    public Uri? Issuer => Uri.TryCreate(PublicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
    public string Resource => Issuer is { } issuer ? new Uri(issuer, OobProtocolRoutes.Mcp).AbsoluteUri : "";
    public bool OAuthConfigured => Supported && Issuer is not null && SigningCertificate is not null && EncryptionCertificate is not null;

    public void LoadCertificates()
    {
        if (!Supported || string.IsNullOrWhiteSpace(SigningCertificatePath) || string.IsNullOrWhiteSpace(EncryptionCertificatePath)) return;
        if (!Path.IsPathFullyQualified(SigningCertificatePath) || !Path.IsPathFullyQualified(EncryptionCertificatePath))
        { CertificateError = "OAuth证书必须使用获准配置中的绝对路径"; return; }
        try
        {
            // macOS 不支持 EphemeralKeySet；DefaultKeySet 不请求持久化或轮换证书。
            var storage = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            SigningCertificate = X509CertificateLoader.LoadPkcs12FromFile(SigningCertificatePath, CertificatePassword,
                storage);
            EncryptionCertificate = X509CertificateLoader.LoadPkcs12FromFile(EncryptionCertificatePath, CertificatePassword,
                storage);
            using var signingRsa = SigningCertificate.GetRSAPrivateKey();
            using var encryptionRsa = EncryptionCertificate.GetRSAPrivateKey();
            if (signingRsa is null || encryptionRsa is null || SigningCertificate.NotBefore.ToUniversalTime() > DateTime.UtcNow
                || EncryptionCertificate.NotBefore.ToUniversalTime() > DateTime.UtcNow
                || SigningCertificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || EncryptionCertificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            {
                CertificateError = "OAuth证书须具有RSA私钥且在有效期内";
                SigningCertificate.Dispose(); EncryptionCertificate.Dispose();
                SigningCertificate = null; EncryptionCertificate = null;
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            CertificateError = "OAuth证书无法读取，请检查获准的宿主配置";
            SigningCertificate?.Dispose(); EncryptionCertificate?.Dispose();
            SigningCertificate = null; EncryptionCertificate = null;
        }
    }
}

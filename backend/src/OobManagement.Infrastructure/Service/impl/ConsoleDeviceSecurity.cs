using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>控制台所有传输沿用单设备证书策略；显式指纹始终优先，不修改系统信任。</summary>
public static class ConsoleDeviceSecurity
{
    public static HttpClient CreateHttpClient(OutOfBandConnection connection)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => ValidateCertificate(connection, certificate, errors);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15), BaseAddress = connection.Endpoint };
    }

    public static void Configure(ClientWebSocket socket, OutOfBandConnection connection)
    {
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) => ValidateCertificate(connection, certificate, errors);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public static bool ValidateCertificate(OutOfBandConnection connection, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        if (!string.IsNullOrWhiteSpace(connection.CertificateFingerprint))
        {
            var fingerprint = connection.CertificateFingerprint.Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            return fingerprint.Length == 64 && fingerprint.All(Uri.IsHexDigit)
                && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(fingerprint), certificate.GetCertHash(HashAlgorithmName.SHA256));
        }
        return connection.IgnoreCertificateErrors || errors == SslPolicyErrors.None;
    }
}

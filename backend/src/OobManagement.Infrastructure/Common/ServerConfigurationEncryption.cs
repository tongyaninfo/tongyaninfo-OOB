using System.Security.Cryptography;
using System.Text;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Common;

/// <summary>版本1固定使用 PBKDF2-SHA256 和 AES-256-GCM；不依赖原宿主 Data Protection 密钥。</summary>
public static class ServerConfigurationEncryption
{
    public const int MaximumPlaintextBytes = 2 * 1024 * 1024;
    private const string Format = "oob-server-configuration";
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes(Format + ":v1");

    public static ServerConfigurationFile Encrypt(byte[] plaintext, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(password, salt);
        try
        {
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
            return new(Format, 1, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>先限制格式与长度，错误密码和篡改统一拒绝，不泄露配置内容。</summary>
    public static bool TryDecrypt(ServerConfigurationFile file, string password, out byte[] plaintext)
    {
        plaintext = [];
        if (file.Format != Format || file.Version != 1 || file.Salt?.Length != 24 || file.Nonce?.Length != 16
            || file.Tag?.Length != 24 || string.IsNullOrEmpty(file.Ciphertext) || file.Ciphertext.Length > 2796204)
            return false;
        byte[]? key = null;
        try
        {
            var salt = Convert.FromBase64String(file.Salt);
            var nonce = Convert.FromBase64String(file.Nonce);
            var tag = Convert.FromBase64String(file.Tag);
            var ciphertext = Convert.FromBase64String(file.Ciphertext);
            if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || ciphertext.Length > MaximumPlaintextBytes) return false;
            key = DeriveKey(password, salt);
            plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
            return true;
        }
        catch (Exception error) when (error is FormatException or CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            plaintext = [];
            return false;
        }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
}

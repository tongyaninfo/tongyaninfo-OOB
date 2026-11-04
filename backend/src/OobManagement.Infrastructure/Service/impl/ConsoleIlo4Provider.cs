using System.Net.Http.Json;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>使用设备随固件提供的 HTML5 解码器；运行时取受限脚本，不重新分发厂商制品。</summary>
public sealed class ConsoleIlo4Provider(Func<OutOfBandConnection, HttpClient>? clientFactory = null,
    IReadOnlyDictionary<string, string>? reviewedAssetHashes = null) : IConsoleConnectionProvider
{
    public string ControllerType => "iLO4";
    public Task<ConsoleCapabilitiesDto> GetCapabilitiesAsync(ConsoleTarget target, CancellationToken ct) =>
        Task.FromResult(new ConsoleCapabilitiesDto(target.ServerId.ToString(), ControllerType,
            IsReviewedFirmware(target.FirmwareVersion) ? "Available" : "Unknown", "NativeHtml5", true, true, null,
            "使用 iLO4 原生 HTML5 视频与 HID 协议；连接时校验固件资源、权限及许可", true));

    /// <summary>库存读取会保留设备原文，例如 iLO 4 v2.82；规范化展示前缀后仍仅放行审查版本。</summary>
    private static bool IsReviewedFirmware(string? firmware) => firmware is not null
        && System.Text.RegularExpressions.Regex.IsMatch(firmware.Trim(), @"^(?:iLO\s+4\s+v)?2\.82$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public async Task<ServiceResult<IConsoleTransport>> OpenAsync(ConsoleTarget target, CancellationToken ct)
    {
        var client = clientFactory?.Invoke(target.Connection) ?? ConsoleDeviceSecurity.CreateHttpClient(target.Connection);
        string? sessionKey = null;
        var transferred = false;
        try
        {
            using var response = await client.PostAsJsonAsync("json/login_session", new
            {
                method = "login", user_login = target.Connection.Username, password = target.Connection.Password
            }, ct);
            if (!response.IsSuccessStatusCode)
                return ServiceResult<IConsoleTransport>.Fail("CONSOLE_AUTHENTICATION_FAILED", "iLO4 控制台认证被拒绝", 409);
            await response.Content.LoadIntoBufferAsync(64 * 1024, ct);
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            if (!json.RootElement.TryGetProperty("session_key", out var key) || key.GetString() is not { Length: 32 } ownedKey
                || !ownedKey.All(char.IsAsciiLetterOrDigit))
                return ServiceResult<IConsoleTransport>.Fail("CONSOLE_AUTHENTICATION_FAILED", "iLO4 未返回有效控制台会话", 409);
            sessionKey = ownedKey;
            var transport = new ConsoleIlo4Transport(target, client, ownedKey, reviewedAssetHashes ?? ConsoleIlo4Assets.ReviewedHashes);
            var initialized = await transport.InitializeAsync(ct);
            if (!initialized.Success)
            {
                await transport.DisposeAsync(); sessionKey = null;
                return ServiceResult<IConsoleTransport>.Fail(initialized.ErrorCode!, initialized.ErrorMessage!, initialized.StatusCode);
            }
            sessionKey = null; transferred = true;
            return ServiceResult<IConsoleTransport>.Ok(transport);
        }
        finally
        {
            if (sessionKey is not null)
            {
                try { using var logout = await client.PostAsJsonAsync("json/login_session", new { method = "logout", session_key = sessionKey }, CancellationToken.None); }
                finally { client.Dispose(); }
            }
            else if (!transferred) client.Dispose();
        }
    }
}

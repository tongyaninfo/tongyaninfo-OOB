using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>复用 iDRAC8 固件自带的 HTML5 解码器；认证、临时凭据与协议过滤留在后端。</summary>
public sealed class ConsoleDellProvider : IConsoleConnectionProvider
{
    private readonly Func<OutOfBandConnection, HttpClient>? _clientFactory;
    private readonly IExceptionRecorder? _exceptions;
    public ConsoleDellProvider() { }
    public ConsoleDellProvider(IExceptionRecorder exceptions) => _exceptions = exceptions;
    public ConsoleDellProvider(Func<OutOfBandConnection, HttpClient> clientFactory) => _clientFactory = clientFactory;
    public string ControllerType => "iDRAC8";

    public Task<ConsoleCapabilitiesDto> GetCapabilitiesAsync(ConsoleTarget target, CancellationToken ct) =>
        Task.FromResult(new ConsoleCapabilitiesDto(target.ServerId.ToString(), ControllerType,
            target.FirmwareVersion is "2.85.85.85" or "2.86.86.86" ? "Available" : "Unknown", "NativeHtml5", true, true, null,
            "连接时核验 iDRAC8 原生解码器、控制台权限和设备会话；本地 ISO 按需流式映射", true));

    public async Task<ServiceResult<IConsoleTransport>> OpenAsync(ConsoleTarget target, CancellationToken ct)
    {
        if (!target.Connection.Endpoint.IsAbsoluteUri || target.Connection.Endpoint.Scheme != "https"
            || !string.IsNullOrEmpty(target.Connection.Endpoint.UserInfo))
            return ServiceResult<IConsoleTransport>.Fail("CONSOLE_ENDPOINT_INVALID", "控制台要求已登记的 HTTPS 管理地址", 409);
        var client = _clientFactory?.Invoke(target.Connection) ?? ConsoleDeviceSecurity.CreateHttpClient(target.Connection);
        client.BaseAddress = new Uri(target.Connection.Endpoint.GetLeftPart(UriPartial.Authority) + "/");
        var transport = new ConsoleDellTransport(target, client);
        try
        {
            var initialized = await transport.InitializeAsync(ct);
            if (initialized.Success) return ServiceResult<IConsoleTransport>.Ok(transport);
            await transport.DisposeAsync();
            return ServiceResult<IConsoleTransport>.Fail(initialized.ErrorCode!, initialized.ErrorMessage!, initialized.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException
            or System.Xml.XmlException or OperationCanceledException or KeyNotFoundException or FormatException)
        {
            var stage = transport.InitializationStage;
            await transport.DisposeAsync();
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
            var code = "CONSOLE_DELL_" + stage + "_FAILED";
            if (_exceptions is not null)
                await _exceptions.RecordAsync(error, new() { Source = "Console", Operation = stage, ErrorCode = code,
                    Message = "iDRAC8 原生控制台初始化失败", ServerId = target.ServerId });
            return ServiceResult<IConsoleTransport>.Fail(code, "iDRAC8 控制台初始化失败（阶段：" + stage + "），未自动重试", 502);
        }
    }
}

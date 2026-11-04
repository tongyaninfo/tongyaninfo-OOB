using OobManagement.Application.Models;
using OobManagement.Application.Service;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>不以管理站点链接冒充 KVM；未接入的固件明确报告阻断原因。</summary>
public sealed class ConsoleUnavailableProvider(string controllerType) : IConsoleConnectionProvider
{
    public string ControllerType => controllerType;
    public Task<ConsoleCapabilitiesDto> GetCapabilitiesAsync(ConsoleTarget target, CancellationToken ct) =>
        Task.FromResult(new ConsoleCapabilitiesDto(target.ServerId.ToString(), ControllerType, "Unavailable", null, false, false,
            "CONSOLE_ADAPTER_UNAVAILABLE", "当前控制器的图形控制台适配尚未通过协议验证，不能建立视频与键鼠连接"));
    public Task<ServiceResult<IConsoleTransport>> OpenAsync(ConsoleTarget target, CancellationToken ct) =>
        Task.FromResult(ServiceResult<IConsoleTransport>.Fail("CONSOLE_ADAPTER_UNAVAILABLE", "当前控制器尚无已验证的图形控制台适配", 409));
}

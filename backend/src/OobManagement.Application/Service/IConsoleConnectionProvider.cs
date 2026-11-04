using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>真实控制台传输与低频遥测分离；未经验证的厂商协议必须明确不可用。</summary>
public interface IConsoleConnectionProvider
{
    string ControllerType { get; }
    Task<ConsoleCapabilitiesDto> GetCapabilitiesAsync(ConsoleTarget target, CancellationToken ct);
    Task<ServiceResult<IConsoleTransport>> OpenAsync(ConsoleTarget target, CancellationToken ct);
}

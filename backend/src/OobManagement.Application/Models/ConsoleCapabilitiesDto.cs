namespace OobManagement.Application.Models;

/// <summary>控制台能力单独报告；Redfish 可达不表示视频、输入或许可已验证。</summary>
public sealed record ConsoleCapabilitiesDto(string ServerId, string ControllerType, string Status, string? Mode,
    bool KeyboardSupported, bool MouseSupported, string? ReasonCode, string? Message, bool LocalMediaSupported = false);

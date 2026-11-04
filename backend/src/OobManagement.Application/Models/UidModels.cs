using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace OobManagement.Application.Models;

/// <summary>UID 灯实际状态；可执行状态仍以本次设备能力为准。</summary>
public enum UidState { Off, Lit, Blinking, Unknown }

/// <summary>可明确执行的 UID 动作；常亮与闪烁不能互相替代，不允许写入未知状态。</summary>
public enum UidControlState { Off, Lit, Blinking }

/// <summary>设备当前 UID 状态及本次核验的控制能力；未确认结果不能以目标状态代替。</summary>
public sealed record UidStatusDto(UidState State, bool CanControl, IReadOnlyList<UidControlState> SupportedStates,
    DateTimeOffset ObservedAt, string? Message = null);

/// <summary>显式确认服务器名称后才改变 UID，缺少 state 不得默认为关灯。</summary>
public sealed record SetUidRequest([property: JsonRequired] UidControlState State, [Required] string Confirmation);

/// <summary>驱动内存结果；诊断只交现有记录链，不向浏览器输出原始异常或认证内容。</summary>
public sealed record UidDeviceResult
{
    public required UidStatusDto Status { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int StatusCode { get; init; } = 200;
    [JsonIgnore] public IReadOnlyList<DeviceDiagnostic> Diagnostics { get; init; } = [];
    [JsonIgnore] public bool Success => ErrorCode is null;
}

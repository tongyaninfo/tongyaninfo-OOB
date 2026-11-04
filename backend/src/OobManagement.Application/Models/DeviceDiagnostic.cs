using System.Text.Json.Serialization;

namespace OobManagement.Application.Models;

/// <summary>驱动的内存诊断；预期拒绝与实际设备故障分开，原始异常只交后端记录器处理。</summary>
public sealed record DeviceDiagnostic(string Code, string SafeMessage, string Category = "Business", [property: JsonIgnore] Exception? Cause = null);

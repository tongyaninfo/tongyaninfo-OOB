using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>删除必须基于调用方实际看到的版本，不覆盖同时发生的编辑。</summary>
public sealed record UserVersionRequest([Range(1, long.MaxValue)] long Version);

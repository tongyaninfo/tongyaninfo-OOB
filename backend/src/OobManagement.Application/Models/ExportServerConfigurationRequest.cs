using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>文件密码只用于本次加密，不保存为账号或设备密码。</summary>
public sealed record ExportServerConfigurationRequest([Required, StringLength(128, MinimumLength = 8)] string Password);

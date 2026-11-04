using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>先验证整份加密配置，再在一个事务中导入；不接受明文设备密码文件。</summary>
public sealed record ImportServerConfigurationRequest(
    [Required, StringLength(128, MinimumLength = 8)] string Password,
    [Required] ServerConfigurationFile File);

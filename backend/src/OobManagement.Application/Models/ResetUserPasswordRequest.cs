using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>管理员重设密码与资料修改分离，原密码和哈希均不可读取。</summary>
public sealed record ResetUserPasswordRequest([Range(1, long.MaxValue)] long Version,
    [Required] string NewPassword, [Required] string ConfirmPassword);

using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>本人改密须校验原密码，成功后重新登录。</summary>
public sealed record ChangePasswordRequest([Required] string CurrentPassword,
    [Required] string NewPassword, [Required] string ConfirmPassword);

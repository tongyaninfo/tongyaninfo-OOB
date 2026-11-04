using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>镜像挂载是显式写操作，服务器名称确认与请求标识均由服务端再次校验。</summary>
public sealed record MountVirtualMediaRequest(string? ImageId, [Required] string SlotId,
    [Required] string Confirmation, [Required, MaxLength(100)] string RequestId, string? ImageUrl = null);

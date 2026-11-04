using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>只允许卸载本平台确认拥有的挂载，不覆盖或拔出其他客户端的介质。</summary>
public sealed record EjectVirtualMediaRequest([Required] string SlotId, [Required] string Confirmation,
    [Required, MaxLength(100)] string RequestId);

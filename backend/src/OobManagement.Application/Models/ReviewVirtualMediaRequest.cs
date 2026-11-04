using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>人工核对仅结束原未知意图，不向设备发送新动作，必须保留核对依据。</summary>
public sealed record ReviewVirtualMediaRequest([Required] string Confirmation, [Required, MaxLength(500)] string Note);

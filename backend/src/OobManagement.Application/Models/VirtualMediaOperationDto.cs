namespace OobManagement.Application.Models;

/// <summary>挂载意图与设备确认分开；未知结果按原 RequestId 查询，不重发。</summary>
public sealed record VirtualMediaOperationDto(string Id, string ServerId, string SlotId, string? ImageId,
    string Action, string Status, string RequestId, string? Message, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool CanReviewNotExecuted = false);

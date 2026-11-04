using System.ComponentModel.DataAnnotations;

namespace OobManagement.Application.Models;

/// <summary>一次明确发送使用稳定请求标识；网络中断后查询原标识，不能换标识自动重试。</summary>
public sealed record MailSendRequest([Required, MaxLength(100)] string RequestId,
    [Required, MaxLength(320)] string ToAddress, [Required, MaxLength(200)] string Subject,
    [Required, MaxLength(20000)] string TextBody)
{
    public override string ToString() => "MailSendRequest { Content = [REDACTED] }";
}

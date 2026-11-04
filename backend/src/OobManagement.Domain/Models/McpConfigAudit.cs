namespace OobManagement.Domain.Models;
public sealed class McpConfigAudit
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public long UserId { get; set; }
    public string Action { get; set; } = "";
    public string ObjectId { get; set; } = "";
    public long PreviousVersion { get; set; }
    public long CurrentVersion { get; set; }
    public string Summary { get; set; } = "";
}

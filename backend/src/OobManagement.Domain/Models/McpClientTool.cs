namespace OobManagement.Domain.Models;
public sealed class McpClientTool
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string ToolName { get; set; } = "";
}

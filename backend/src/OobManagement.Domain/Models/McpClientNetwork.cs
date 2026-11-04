namespace OobManagement.Domain.Models;
public sealed class McpClientNetwork
{
    public long Id { get; set; }
    public long ClientId { get; set; }
    public string Network { get; set; } = "";
    public string AddressFamily { get; set; } = "";
}

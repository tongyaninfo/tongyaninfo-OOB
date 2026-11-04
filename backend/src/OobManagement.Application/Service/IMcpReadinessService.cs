using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

public interface IMcpReadinessService
{
    Task<McpHostStatus> ReadAsync(CancellationToken ct);
}

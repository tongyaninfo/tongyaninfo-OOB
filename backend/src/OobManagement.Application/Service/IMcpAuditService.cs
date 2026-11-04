using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

public interface IMcpAuditService
{
    Task<ServiceResult<string>> StartAsync(McpCallStart request, CancellationToken ct);
    Task CompleteAsync(string callId, McpCallCompletion result, CancellationToken ct);
    Task ReplayAsync(CancellationToken ct);
}

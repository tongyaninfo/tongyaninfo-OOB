using System.Text.Json;
using OobManagement.Application.Models;

namespace OobManagement.Application.Service;

/// <summary>仅查询保存数据；执行前后重新核对策略，不持有或调用设备驱动。</summary>
public interface IMcpQueryService
{
    Task<ServiceResult<JsonElement>> ExecuteAsync(McpAccessContext context, string tool, JsonElement arguments, CancellationToken ct);
}

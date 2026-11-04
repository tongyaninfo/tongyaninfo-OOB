using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OobManagement.Application.Common;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl;

public sealed partial class ServerManagementService
{
    public async Task<ServiceResult<PowerCapabilitiesDto>> GetPowerCapabilitiesAsync(long id, UserSessionContext actor, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<PowerCapabilitiesDto>(actor, UserPermissions.PowerOperations, ct);
        if (denied is not null) return denied;
        var gate = Gate(id);
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(90), ct))
            return ServiceResult<PowerCapabilitiesDto>.Fail("SERVER_BUSY", "等待服务器当前操作结束超时，请重新读取电源能力。", 409);
        try
        {
            denied = await AuthorizeAsync<PowerCapabilitiesDto>(actor, UserPermissions.PowerOperations, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, ct);
            if (server is null) return Missing<PowerCapabilitiesDto>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            // 专用读取只访问主机及必要动作说明，不依赖遥测/硬件资源，也不保存缓存或生成电源意图。
            var result = await Driver(server.ControllerType).ReadPowerCapabilitiesAsync(Connection(server), deadline.Token);
            await RecordDiagnosticsAsync(result.Diagnostics, "ReadPowerCapabilities", id);
            denied = await AuthorizeAsync<PowerCapabilitiesDto>(actor, UserPermissions.PowerOperations, ct);
            if (denied is not null) return denied;
            if (!result.Success)
                return ServiceResult<PowerCapabilitiesDto>.Fail(result.ErrorCode ?? "POWER_CAPABILITIES_FAILED", result.ErrorMessage ?? "无法读取电源能力，请稍后刷新。", 502);
            return ServiceResult<PowerCapabilitiesDto>.Ok(new(configuration.GetValue<bool>("PowerControl:Enabled"), result.Capabilities, result.Identity.PowerState));
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await exceptions.RecordAsync(error, new() { Kind = "DeviceFailure", Source = "Power", Operation = "ReadPowerCapabilities", ErrorCode = "POWER_CAPABILITIES_FAILED", Message = "电源能力读取未完成", ServerId = id });
            return ServiceResult<PowerCapabilitiesDto>.Fail("POWER_CAPABILITIES_FAILED", "无法读取电源能力，请稍后刷新。", 502);
        }
        finally { gate.Release(); }
    }
}

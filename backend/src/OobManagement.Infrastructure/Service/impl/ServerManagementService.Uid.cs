using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OobManagement.Application.Common;
using OobManagement.Application.Models;

namespace OobManagement.Infrastructure.Service.impl;

public sealed partial class ServerManagementService
{
    public async Task<ServiceResult<UidStatusDto>> GetUidAsync(long id, UserSessionContext actor, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<UidStatusDto>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        var gate = Gate(id);
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(15), ct)) return ServiceResult<UidStatusDto>.Fail("SERVER_BUSY", "服务器正在处理其他操作，请稍后查询 UID。", 409);
        try
        {
            denied = await AuthorizeAsync<UidStatusDto>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, ct);
            if (server is null) return Missing<UidStatusDto>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var result = await Driver(server.ControllerType).ReadUidAsync(Connection(server), deadline.Token);
            await RecordDiagnosticsAsync(result.Diagnostics, "ReadUid", id);
            denied = await AuthorizeAsync<UidStatusDto>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            return new(result.Status, result.ErrorCode, result.ErrorMessage, result.StatusCode);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await exceptions.RecordAsync(error, new() { Kind = "DeviceFailure", Source = "Uid", Operation = "ReadUid", ErrorCode = "UID_READ_FAILED", Message = "UID 状态读取未完成", ServerId = id });
            return new(new(UidState.Unknown, false, [], DateTimeOffset.UtcNow, "UID 状态暂时不可读取。"), "UID_READ_FAILED", "UID 状态暂时不可读取。", 502);
        }
        finally { gate.Release(); }
    }

    public async Task<ServiceResult<UidStatusDto>> SetUidAsync(long id, UserSessionContext actor, SetUidRequest request, CancellationToken ct)
    {
        var denied = await AuthorizeAsync<UidStatusDto>(actor, UserPermissions.ServerManagement, ct);
        if (denied is not null) return denied;
        var targetState = request.State switch
        {
            UidControlState.Off => UidState.Off, UidControlState.Lit => UidState.Lit,
            UidControlState.Blinking => UidState.Blinking, _ => UidState.Unknown
        };
        if (targetState == UidState.Unknown) return ServiceResult<UidStatusDto>.Fail("UID_INVALID_STATE", "UID 只允许关灯、常亮或闪烁，具体以设备能力为准。", 400);
        var gate = Gate(id);
        // 与采样继续串行，但显式 UID 操作进入等待队列，避免秒级采样让瞬时抢锁反复失败。
        // 等待上限与单轮设备采集截止一致；取消或超时只撤销待发请求，不在后台补发。
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(90), ct)) return ServiceResult<UidStatusDto>.Fail("SERVER_BUSY", "等待服务器其他操作结束超时，本次未发送 UID 命令。", 409);
        var driverInvoked = false;
        try
        {
            denied = await AuthorizeAsync<UidStatusDto>(actor, UserPermissions.ServerManagement, ct);
            if (denied is not null) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(value => value.Id == id, ct);
            if (server is null) return Missing<UidStatusDto>();
            if (request.Confirmation != server.Name) return ServiceResult<UidStatusDto>.Fail("CONFIRMATION_REQUIRED", "服务器名称已变化，请刷新服务器信息后重新操作 UID。", 400);
            var driver = Driver(server.ControllerType);
            var connection = Connection(server);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            // UID 与电源控制开关独立；只有本次显式点击并核对目标后才调用一次驱动写入口。
            driverInvoked = true;
            var result = await driver.SetUidAsync(connection, targetState, deadline.Token,
                async token => await AuthorizeAsync<bool>(actor, UserPermissions.ServerManagement, token) ?? ServiceResult<bool>.Ok(true));
            await RecordDiagnosticsAsync(result.Diagnostics, "SetUid", id);
            if (result.Success) logger.LogInformation("UID 状态已核对，服务器标识 {ServerId}，用户标识 {UserId}，状态 {UidState}", id, actor.UserId, result.Status.State);
            return new(result.Status, result.ErrorCode, result.ErrorMessage, result.StatusCode);
        }
        catch (Exception error) when (driverInvoked || error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var code = driverInvoked ? "UID_RESULT_UNKNOWN" : "UID_NOT_SENT";
            var message = driverInvoked ? "UID 操作结果未确认，请查询当前状态，勿自动重发。" : "UID 操作准备未完成，未发送设备命令。";
            await exceptions.RecordAsync(error, new() { Kind = "DeviceFailure", Source = "Uid", Operation = "SetUid", ErrorCode = code, Message = message, ServerId = id, UserId = actor.UserId });
            return new(new(UidState.Unknown, false, [], DateTimeOffset.UtcNow, message), code, message, 502);
        }
        finally { gate.Release(); }
    }
}

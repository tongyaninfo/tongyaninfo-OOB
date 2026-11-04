using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>复用邮件两表保存计划和执行快照；同一周期的意图不会因改收件人或重启再次创建。</summary>
public sealed class MailReportService(IDbContextFactory<OobDbContext> factory, IAuthenticationService authentication,
    IOperationReportBuilder reports, IMailSystemSender sender, TimeProvider clock, ILogger<MailReportService> logger) : IMailReportService
{
    private readonly SemaphoreSlim _dispatch = new(1, 1);

    public async Task<ServiceResult<MailReportConfigurationDto>> ConfigurationAsync(UserSessionContext actor, CancellationToken ct)
    {
        if (await AuthorizeAsync<MailReportConfigurationDto>(actor, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        var configuration = await db.Set<MailConfiguration>().AsNoTracking().SingleOrDefaultAsync(x => x.Slot == 1, ct);
        try { return ServiceResult<MailReportConfigurationDto>.Ok(View(configuration)); }
        catch (JsonException) { return InvalidConfiguration<MailReportConfigurationDto>(); }
    }

    public async Task<ServiceResult<MailReportConfigurationDto>> SaveAsync(UserSessionContext actor, MailReportConfigurationRequest request, CancellationToken ct)
    {
        if (await AuthorizeAsync<MailReportConfigurationDto>(actor, ct) is { } rejected) return rejected;
        if (request.Version < 1 || !MailReportRules.Validate(request.Schedules))
            return ServiceResult<MailReportConfigurationDto>.Fail("REPORT_INVALID_CONFIGURATION", "请完整配置日、周、月计划，以及有效的收件邮箱、发送时间和日期");
        await using var db = await factory.CreateDbContextAsync(ct);
        var configuration = await db.Set<MailConfiguration>().SingleOrDefaultAsync(x => x.Slot == 1, ct);
        if (configuration is null) return ServiceResult<MailReportConfigurationDto>.Fail("MAIL_NOT_CONFIGURED", "请先保存SMTP配置", 409);
        if (configuration.Version != request.Version) return Conflict();
        MailReportPlan[] original;
        try { original = MailReportRules.Read(configuration.ReportSchedulesJson); }
        catch (JsonException) { return InvalidConfiguration<MailReportConfigurationDto>(); }
        var now = clock.GetUtcNow();
        var changed = false;
        var plans = original.Select(old =>
        {
            var next = request.Schedules.Single(x => x.Period == old.Period);
            if (old.Enabled == next.Enabled && old.ToAddress == next.ToAddress && old.SendTime == next.SendTime
                && old.DayOfWeek == next.DayOfWeek && old.DayOfMonth == next.DayOfMonth) return old;
            changed = true;
            // 计划变更只对未来触发生效，不能在保存动作中补发过去邮件。
            return new MailReportPlan(next.Period, next.Enabled, next.ToAddress, next.SendTime,
                next.DayOfWeek, next.DayOfMonth, next.Enabled ? now : null);
        }).ToArray();
        if (await AuthorizeAsync<MailReportConfigurationDto>(actor, ct) is { } revoked) return revoked;
        if (changed)
        {
            configuration.ReportSchedulesJson = JsonSerializer.Serialize(plans);
            configuration.Version++;
            configuration.UpdatedAt = now;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
        }
        return ServiceResult<MailReportConfigurationDto>.Ok(View(configuration));
    }

    public async Task<ServiceResult<MailReportPreviewDto>> PreviewAsync(UserSessionContext actor, MailReportPreviewRequest request, CancellationToken ct)
    {
        if (await AuthorizeAsync<MailReportPreviewDto>(actor, ct) is { } rejected) return rejected;
        var result = await reports.BuildAsync(request.From, request.To, "运行情况分析", ct);
        if (await AuthorizeAsync<MailReportPreviewDto>(actor, ct) is { } revoked) return revoked;
        return result;
    }

    public async Task DispatchDueAsync(CancellationToken ct)
    {
        if (!await _dispatch.WaitAsync(0, ct)) return;
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var configuration = await db.Set<MailConfiguration>().AsNoTracking().SingleOrDefaultAsync(x => x.Slot == 1, ct);
            if (configuration is null) return;
            var now = clock.GetUtcNow();
            foreach (var plan in MailReportRules.Read(configuration.ReportSchedulesJson))
            {
                if (MailReportRules.LatestDue(plan, now) is not { } due) continue;
                var (from, to) = MailReportRules.Window(plan.Period, due);
                var requestId = MailReportRules.RequestId(plan.Period, from, to);
                // 先查冻结意图，再计算正文；未知和明确失败均不自动重发。
                if (await db.Set<MailMessage>().AsNoTracking().AnyAsync(x => x.RequestId == requestId, ct)) continue;
                var report = await reports.BuildAsync(from, to, MailReportRules.Title(plan.Period), ct);
                if (!report.Success)
                {
                    logger.LogWarning("运行报表生成失败：{Period} {Code}", plan.Period, report.ErrorCode);
                    continue;
                }
                var body = report.Data!;
                var result = await sender.SendReportAsync(new(requestId, plan.ToAddress, body.Subject, body.TextBody), body.HtmlBody,
                    configuration.Version, token => AuthorizePlanAsync(configuration.Version, plan, token), ct);
                if (!result.Success) logger.LogWarning("运行报表未提交：{Period} {Code}", plan.Period, result.ErrorCode);
            }
        }
        finally { _dispatch.Release(); }
    }

    private async Task<ServiceResult<bool>> AuthorizePlanAsync(long version, MailReportPlan expected, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var current = await db.Set<MailConfiguration>().AsNoTracking().SingleOrDefaultAsync(x => x.Slot == 1, ct);
        if (current?.Version != version) return ServiceResult<bool>.Fail("REPORT_PLAN_CHANGED", "报表或SMTP配置已变化，本次尚未提交邮件", 409);
        var plan = MailReportRules.Read(current.ReportSchedulesJson).Single(x => x.Period == expected.Period);
        return plan.Enabled && plan == expected ? ServiceResult<bool>.Ok(true)
            : ServiceResult<bool>.Fail("REPORT_PLAN_CHANGED", "报表计划已停用或变化，本次尚未提交邮件", 409);
    }

    private MailReportConfigurationDto View(MailConfiguration? configuration) => new(configuration is not null,
        configuration?.Version ?? 0, MailReportRules.Read(configuration?.ReportSchedulesJson ?? "[]").Select(x =>
            new MailReportScheduleDto(x.Period, x.Enabled, x.ToAddress, x.SendTime, x.DayOfWeek, x.DayOfMonth,
                x.EffectiveFrom, MailReportRules.NextDue(x, clock.GetUtcNow()))).ToArray());

    private async Task<ServiceResult<T>?> AuthorizeAsync<T>(UserSessionContext actor, CancellationToken ct)
    {
        var session = await authentication.ValidateSessionAsync(actor.UserId, actor.AuthVersion, ct);
        if (session is null) return ServiceResult<T>.Fail("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        return session.User.IsAdministrator ? null : ServiceResult<T>.Fail("FORBIDDEN", "仅系统管理员可以使用运行报表", 403);
    }

    private static ServiceResult<MailReportConfigurationDto> Conflict() => ServiceResult<MailReportConfigurationDto>.Fail(
        "MAIL_CONFIGURATION_CONFLICT", "配置已变化，请刷新后重试", 409);
    private static ServiceResult<T> InvalidConfiguration<T>() => ServiceResult<T>.Fail(
        "REPORT_CONFIGURATION_UNAVAILABLE", "报表计划数据无效，请检查配置记录", 503);
}

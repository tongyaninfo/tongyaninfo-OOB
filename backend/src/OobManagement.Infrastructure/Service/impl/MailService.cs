using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>唯一SMTP配置和直接发件；先记录稳定意图再发送一次，不在查询、启动或恢复时制造发送动作。</summary>
public sealed class MailService(IDbContextFactory<OobDbContext> factory, IIdService ids,
    IAuthenticationService authentication, IMailTransport transport, IDataProtectionProvider protection,
    MailSendState state, IOptions<MailOptions> options) : IMailService, IMailSystemSender
{
    private readonly IDataProtector _protector = protection.CreateProtector("OobManagement.MailCredentials.v1");

    public async Task<ServiceResult<MailConfigurationDto>> ConfigurationAsync(UserSessionContext actor, CancellationToken ct)
    {
        if (await AuthorizeAsync<MailConfigurationDto>(actor, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        return ServiceResult<MailConfigurationDto>.Ok(MailRules.View(await Configuration(db, ct)));
    }

    public async Task<ServiceResult<MailConfigurationDto>> SaveConfigurationAsync(UserSessionContext actor,
        MailConfigurationRequest request, CancellationToken ct)
    {
        if (await AuthorizeAsync<MailConfigurationDto>(actor, ct) is { } rejected) return rejected;
        var valid = MailRules.Validate(request);
        if (!valid.Success) return Failure<MailConfigurationDto, bool>(valid);
        if (!options.Value.AllowedPorts.Contains(request.Port))
            return ServiceResult<MailConfigurationDto>.Fail("MAIL_PORT_NOT_ALLOWED", "此SMTP端口未获宿主配置允许");
        await using var db = await factory.CreateDbContextAsync(ct);
        var configuration = await db.Set<MailConfiguration>().SingleOrDefaultAsync(x => x.Slot == 1, ct);
        if ((configuration?.Version ?? 0) != request.Version)
            return ServiceResult<MailConfigurationDto>.Fail("MAIL_CONFIGURATION_CONFLICT", "配置已变化，请刷新后重试", 409);
        if (string.IsNullOrEmpty(request.Password) && string.IsNullOrEmpty(configuration?.PasswordCiphertext))
            return ServiceResult<MailConfigurationDto>.Fail("MAIL_PASSWORD_REQUIRED", "首次配置请输入SMTP密码或邮箱授权码");
        string ciphertext;
        try { ciphertext = string.IsNullOrEmpty(request.Password) ? configuration!.PasswordCiphertext : _protector.Protect(request.Password); }
        catch (Exception ex) when (ProtectionFailure(ex))
        { return ServiceResult<MailConfigurationDto>.Fail("MAIL_CREDENTIALS_UNAVAILABLE", "邮件凭据保护不可用，请检查宿主密钥配置", 503); }
        // 加密可能访问宿主密钥；落库之前重新校验身份，不能沿用准备阶段的授权。
        if (await AuthorizeAsync<MailConfigurationDto>(actor, ct) is { } changed) return changed;
        if (configuration is null)
        {
            configuration = new() { Id = ids.NextId(), Slot = 1 };
            db.Add(configuration);
        }
        else configuration.Version++;
        configuration.Host = request.Host.Trim(); configuration.Port = request.Port; configuration.SecurityMode = request.SecurityMode;
        configuration.FromAddress = request.FromAddress.Trim(); configuration.Username = request.Username?.Trim() ?? "";
        configuration.PasswordCiphertext = ciphertext; configuration.UpdatedAt = DateTimeOffset.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        { return ServiceResult<MailConfigurationDto>.Fail("MAIL_CONFIGURATION_CONFLICT", "配置已变化，请刷新后重试", 409); }
        catch (DbUpdateException ex) when (db.Storage.IsUniqueConflict(ex, "IX_OOB_MAIL_CONFIGURATION_Slot"))
        { return ServiceResult<MailConfigurationDto>.Fail("MAIL_CONFIGURATION_CONFLICT", "配置已由其他操作创建，请刷新后重试", 409); }
        return ServiceResult<MailConfigurationDto>.Ok(MailRules.View(configuration));
    }

    public async Task<ServiceResult<MailProbeDto>> ProbeAsync(UserSessionContext actor, CancellationToken ct)
    {
        if (await AuthorizeAsync<MailProbeDto>(actor, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        var configuration = await Configuration(db, ct);
        var endpoint = Endpoint(configuration);
        if (!endpoint.Success) return Failure<MailProbeDto, MailTransportConfiguration>(endpoint);
        if (await AuthorizeAsync<MailProbeDto>(actor, ct) is { } changed) return changed;
        var result = await transport.ProbeAsync(endpoint.Data!, ct);
        return ServiceResult<MailProbeDto>.Ok(new(result.Status == "Accepted", result.ErrorCode, result.Message));
    }

    public Task<ServiceResult<MailMessageDto>> SendTestAsync(UserSessionContext actor, MailSendRequest request, CancellationToken ct) =>
        SendAsync(actor, "Test", request, ct);
    public Task<ServiceResult<MailMessageDto>> SendManualAsync(UserSessionContext actor, MailSendRequest request, CancellationToken ct) =>
        SendAsync(actor, "Manual", request, ct);
    public Task<ServiceResult<MailMessageDto>> SendSystemAsync(MailSendRequest request, CancellationToken ct) =>
        SendAsync(null, "System", request, ct);

    public Task<ServiceResult<MailMessageDto>> SendReportAsync(MailSendRequest request, string htmlBody,
        long configurationVersion, Func<CancellationToken, Task<ServiceResult<bool>>> authorizeReport, CancellationToken ct)
    {
        var validHtml = MailRules.ValidateHtml(htmlBody);
        if (!validHtml.Success) return Task.FromResult(Failure<MailMessageDto, bool>(validHtml));
        if (authorizeReport is null)
            return Task.FromResult(ServiceResult<MailMessageDto>.Fail("MAIL_REPORT_AUTHORIZATION_REQUIRED", "运行报表必须核验有效计划", 403));
        return SendAsync(null, "System", request, ct, htmlBody, configurationVersion, authorizeReport);
    }

    private async Task<ServiceResult<MailMessageDto>> SendAsync(UserSessionContext? actor, string kind,
        MailSendRequest request, CancellationToken ct, string? htmlBody = null, long? expectedVersion = null,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeReport = null)
    {
        if (actor is { } subject && await AuthorizeAsync<MailMessageDto>(subject, ct) is { } rejected) return rejected;
        var valid = MailRules.Validate(request);
        if (!valid.Success) return Failure<MailMessageDto, bool>(valid);
        if (htmlBody is not null)
        {
            var validHtml = MailRules.ValidateHtml(htmlBody);
            if (!validHtml.Success) return Failure<MailMessageDto, bool>(validHtml);
        }
        else if (authorizeReport is not null)
            return ServiceResult<MailMessageDto>.Fail("MAIL_INVALID_HTML_BODY", "运行报表缺少HTML正文");
        var hash = MailRules.RequestHash(request, kind, actor?.UserId, htmlBody);
        await using var db = await factory.CreateDbContextAsync(ct);
        var original = await db.Set<MailMessage>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == request.RequestId, ct);
        if (original is not null) return Existing(original, hash);
        var configuration = await Configuration(db, ct);
        if (expectedVersion.HasValue && configuration?.Version != expectedVersion.Value)
            return ServiceResult<MailMessageDto>.Fail("MAIL_CONFIGURATION_CHANGED", "SMTP或报表配置已变化，本次尚未提交邮件", 409);
        var endpoint = Endpoint(configuration);
        if (!endpoint.Success) return Failure<MailMessageDto, MailTransportConfiguration>(endpoint);
        // 生成报告和读取凭据都可能耗时；持久化意图前不能沿用之前读取的启用状态或身份。
        var authorized = await AuthorizeSendAsync(actor, configuration!.Version, ct, authorizeReport);
        if (!authorized.Success || !authorized.Data) return Failure<MailMessageDto, bool>(authorized);
        var message = new MailMessage
        {
            Id = ids.NextId(), RequestId = request.RequestId, RequestHash = hash, Kind = kind,
            FromAddress = configuration!.FromAddress, ToAddress = request.ToAddress.Trim(), Subject = request.Subject,
            TextBody = request.TextBody, HtmlBody = htmlBody, CreatedByUserId = actor?.UserId, Status = "Sending", CreatedAt = DateTimeOffset.UtcNow,
            Message = "正在提交邮件服务器，请按原请求标识查询结果"
        };
        state.Start(message.Id);
        try
        {
            db.Add(message);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (db.Storage.IsUniqueConflict(ex, "IX_OOB_MAIL_MESSAGE_RequestId"))
            {
                db.ChangeTracker.Clear();
                original = await db.Set<MailMessage>().AsNoTracking().SingleAsync(x => x.RequestId == request.RequestId, ct);
                return Existing(original, hash);
            }
            MailTransportResult result;
            try
            {
                result = await transport.SendAsync(endpoint.Data!, new($"oob-{message.Id}@mail.local", message.ToAddress,
                    message.Subject, message.TextBody, message.CreatedAt, message.HtmlBody), ct,
                    async token => await AuthorizeSendAsync(actor, configuration.Version, token, authorizeReport));
            }
            catch (Exception)
            {
                // 适配器出现未预期故障也不能猜测远端未发送，错误和正文不进入普通日志。
                result = new("Unknown", "MAIL_RESULT_UNKNOWN", "发送结果未知，请按原请求核对；系统不会自动重发");
            }
            if (result.Status is not ("Accepted" or "Failed" or "Unknown"))
                result = new("Unknown", "MAIL_RESULT_UNKNOWN", "无法确认邮件服务器结果，请按原请求核对");
            message.Status = result.Status; message.ErrorCode = result.ErrorCode; message.Message = result.Message;
            message.CompletedAt = DateTimeOffset.UtcNow;
            // HTTP取消不抹去已经发生的协议事实；独立短期限尽力保存，不重放任何SMTP动作。
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await db.SaveChangesAsync(completion.Token); }
            catch (Exception)
            {
                message.Status = "Unknown"; message.ErrorCode = "MAIL_RESULT_NOT_SAVED";
                message.Message = "本地未能保存最终结果，远端结果待核对；系统不会自动重发"; message.CompletedAt = null;
            }
            return ServiceResult<MailMessageDto>.Ok(MailRules.View(message, true, false));
        }
        finally { state.Finish(message.Id); }
    }

    public async Task<ServiceResult<PageData<MailMessageDto>>> ListAsync(UserSessionContext actor, MailMessageQuery request, CancellationToken ct)
    {
        if (await AuthorizeAsync<PageData<MailMessageDto>>(actor, ct) is { } rejected) return rejected;
        if (request.Current is < 1 or > 1000000 || request.PageSize is < 1 or > 100)
            return ServiceResult<PageData<MailMessageDto>>.Fail("INVALID_PAGE", "分页参数超出范围");
        if (request.Keyword?.Trim().Length > 100)
            return ServiceResult<PageData<MailMessageDto>>.Fail("INVALID_KEYWORD", "搜索文字不能超过100字符");
        var activeIds = state.ActiveIds;
        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.Set<MailMessage>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(request.Keyword))
        {
            var keyword = request.Keyword.Trim().ToLowerInvariant();
            query = query.Where(x => x.Subject.ToLower().Contains(keyword) || x.ToAddress.ToLower().Contains(keyword));
        }
        query = request.Status switch
        {
            null or "" or "all" => query,
            "Unknown" => query.Where(x => x.Status == "Unknown" || x.Status == "Sending" && !activeIds.Contains(x.Id)),
            "Sending" => query.Where(x => x.Status == "Sending" && activeIds.Contains(x.Id)),
            "Accepted" or "Failed" => query.Where(x => x.Status == request.Status),
            _ => null
        };
        if (query is null) return ServiceResult<PageData<MailMessageDto>>.Fail("INVALID_STATUS", "发件状态筛选不正确");
        if (request.Kind is not (null or "" or "all"))
        {
            if (request.Kind is not ("Test" or "Manual" or "System")) return ServiceResult<PageData<MailMessageDto>>.Fail("INVALID_KIND", "发件类型筛选不正确");
            query = query.Where(x => x.Kind == request.Kind);
        }
        var descending = request.SortOrder is null or "" or "descend";
        if (!descending && request.SortOrder != "ascend") return ServiceResult<PageData<MailMessageDto>>.Fail("INVALID_SORT", "排序方向不正确");
        IOrderedQueryable<MailMessage>? ordered = (request.SortField, descending) switch
        {
            (null or "" or "createdAt", true) => query.OrderByDescending(x => x.CreatedAt),
            (null or "" or "createdAt", false) => query.OrderBy(x => x.CreatedAt),
            ("subject", true) => query.OrderByDescending(x => x.Subject), ("subject", false) => query.OrderBy(x => x.Subject),
            ("toAddress", true) => query.OrderByDescending(x => x.ToAddress), ("toAddress", false) => query.OrderBy(x => x.ToAddress),
            ("kind", true) => query.OrderByDescending(x => x.Kind), ("kind", false) => query.OrderBy(x => x.Kind),
            ("completedAt", true) => query.OrderByDescending(x => x.CompletedAt),
            ("completedAt", false) => query.OrderBy(x => x.CompletedAt),
            ("status", true) => query.OrderByDescending(x => x.Status == "Sending" && !activeIds.Contains(x.Id) ? "Unknown" : x.Status),
            ("status", false) => query.OrderBy(x => x.Status == "Sending" && !activeIds.Contains(x.Id) ? "Unknown" : x.Status),
            _ => null
        };
        if (ordered is null) return ServiceResult<PageData<MailMessageDto>>.Fail("INVALID_SORT", "排序字段不正确");
        var total = await query.CountAsync(ct);
        // 正文只由详情入口读取，列表查询本身也不从数据库取出正文。
        var rows = await ordered.ThenByDescending(x => x.Id).Skip((request.Current - 1) * request.PageSize).Take(request.PageSize)
            .Select(x => new MailMessage { Id = x.Id, RequestId = x.RequestId, Kind = x.Kind, FromAddress = x.FromAddress,
                ToAddress = x.ToAddress, Subject = x.Subject, Status = x.Status, ErrorCode = x.ErrorCode, Message = x.Message,
                CreatedByUserId = x.CreatedByUserId, CreatedAt = x.CreatedAt, CompletedAt = x.CompletedAt }).ToArrayAsync(ct);
        return ServiceResult<PageData<MailMessageDto>>.Ok(new(rows.Select(x => MailRules.View(x, false, activeIds.Contains(x.Id))).ToArray(),
            total, request.Current, request.PageSize));
    }

    public async Task<ServiceResult<MailMessageDto>> GetAsync(UserSessionContext actor, long id, CancellationToken ct)
    {
        if (await AuthorizeAsync<MailMessageDto>(actor, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Set<MailMessage>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return Found(row);
    }

    public async Task<ServiceResult<MailMessageDto>> GetByRequestAsync(UserSessionContext actor, string requestId, CancellationToken ct)
    {
        if (await AuthorizeAsync<MailMessageDto>(actor, ct) is { } rejected) return rejected;
        if (!MailRules.ValidRequestId(requestId)) return ServiceResult<MailMessageDto>.Fail("MAIL_INVALID_REQUEST_ID", "发送请求标识格式不正确");
        await using var db = await factory.CreateDbContextAsync(ct);
        return Found(await db.Set<MailMessage>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == requestId, ct));
    }

    private ServiceResult<MailMessageDto> Found(MailMessage? row) => row is null
        ? ServiceResult<MailMessageDto>.Fail("MAIL_MESSAGE_NOT_FOUND", "发件记录不存在", 404)
        : ServiceResult<MailMessageDto>.Ok(MailRules.View(row, true, state.IsActive(row.Id)));

    private ServiceResult<MailMessageDto> Existing(MailMessage row, string hash) => row.RequestHash != hash
        ? ServiceResult<MailMessageDto>.Fail("MAIL_REQUEST_CONFLICT", "此请求标识已用于其他内容或操作，请核对原发件记录", 409)
        : Found(row);

    private async Task<ServiceResult<bool>> AuthorizeSendAsync(UserSessionContext? actor, long version, CancellationToken ct,
        Func<CancellationToken, Task<ServiceResult<bool>>>? authorizeReport = null)
    {
        if (actor is { } subject && await AuthorizeAsync<bool>(subject, ct) is { } rejected) return rejected;
        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Set<MailConfiguration>().AsNoTracking().AnyAsync(x => x.Slot == 1 && x.Version == version, ct))
            return ServiceResult<bool>.Fail("MAIL_CONFIGURATION_CHANGED", "SMTP或报表配置已变化，本次尚未提交邮件", 409);
        if (authorizeReport is null) return ServiceResult<bool>.Ok(true);
        try
        {
            var result = await authorizeReport(ct);
            return result.Success && !result.Data
                ? ServiceResult<bool>.Fail("MAIL_REPORT_DISABLED", "报表计划已停用，本次尚未提交邮件", 409)
                : result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return ServiceResult<bool>.Fail("MAIL_REPORT_AUTHORIZATION_UNAVAILABLE", "无法核验报表计划，本次尚未提交邮件", 503);
        }
    }

    private async Task<ServiceResult<T>?> AuthorizeAsync<T>(UserSessionContext actor, CancellationToken ct)
    {
        var current = await authentication.ValidateSessionAsync(actor.UserId, actor.AuthVersion, ct);
        if (current is null) return ServiceResult<T>.Fail("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        return current.User.IsAdministrator ? null : ServiceResult<T>.Fail("FORBIDDEN", "仅系统管理员可以使用邮件功能", 403);
    }

    private static Task<MailConfiguration?> Configuration(OobDbContext db, CancellationToken ct) =>
        db.Set<MailConfiguration>().AsNoTracking().SingleOrDefaultAsync(x => x.Slot == 1, ct);

    private ServiceResult<MailTransportConfiguration> Endpoint(MailConfiguration? configuration)
    {
        if (configuration is null) return ServiceResult<MailTransportConfiguration>.Fail("MAIL_NOT_CONFIGURED", "请先保存SMTP配置", 409);
        try
        {
            var password = _protector.Unprotect(configuration.PasswordCiphertext);
            if (password.Length == 0) return ServiceResult<MailTransportConfiguration>.Fail("MAIL_PASSWORD_REQUIRED", "请配置SMTP密码或邮箱授权码", 409);
            return ServiceResult<MailTransportConfiguration>.Ok(new(configuration.Host, configuration.Port, configuration.SecurityMode,
                configuration.FromAddress, string.IsNullOrEmpty(configuration.Username) ? configuration.FromAddress : configuration.Username, password));
        }
        catch (Exception ex) when (ProtectionFailure(ex))
        { return ServiceResult<MailTransportConfiguration>.Fail("MAIL_CREDENTIALS_UNAVAILABLE", "无法读取邮件凭据，请恢复原宿主密钥或明确重新保存密码", 503); }
    }

    private static bool ProtectionFailure(Exception ex) => ex is CryptographicException or IOException
        or UnauthorizedAccessException or InvalidOperationException or FormatException;

    private static ServiceResult<T> Failure<T, TSource>(ServiceResult<TSource> source) =>
        ServiceResult<T>.Fail(source.ErrorCode!, source.ErrorMessage!, source.StatusCode);
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using OobManagement.Infrastructure.Common;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>镜像流式落盘，介质意图先追加持久记录再发送；不迁库、不删文件、不随控制台断开拔盘。</summary>
public sealed class VirtualMediaService(IConsoleTargetResolver targets, IEnumerable<IVirtualMediaDriver> drivers,
    IIdService ids, VirtualMediaOptions options, IDataProtectionProvider protection, IAuthenticationService authentication) : IVirtualMediaService, IDisposable
{
    private readonly SemaphoreSlim _storeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _serverGates = new();
    private readonly ConcurrentDictionary<long, ReadTraffic> _traffic = new();
    private readonly Dictionary<string, VirtualMediaImageDto> _images = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JournalOperation> _operations = new(StringComparer.Ordinal);
    private readonly IDataProtector _protector = protection.CreateProtector("OobManagement.VirtualMediaSources.v1");
    private bool _loaded;
    private bool _healthy = true;
    private long _reservedBytes;
    private int _activeUploads;
    private FileStream? _writerLease;
    private string JournalPath => Path.Combine(options.StorageDirectory, "media-journal.jsonl");
    private static string Key(long server, long user, string request) => $"{server}:{user}:{request}";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private IVirtualMediaDriver? Driver(ConsoleTarget target) => drivers.SingleOrDefault(x => x.ControllerType == target.ControllerType);
    private static ServiceResult<T> Fail<T>(string code, string message, int status = 400) => ServiceResult<T>.Fail(code, message, status);

    private async Task<ServiceResult<bool>> AuthorizeAsync(long userId, long authVersion, CancellationToken ct)
    {
        var session = await authentication.ValidateSessionAsync(userId, authVersion, ct);
        if (session is null) return Fail<bool>("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        return session.User.CanUseConsole ? ServiceResult<bool>.Ok(true) : Fail<bool>("FORBIDDEN", "无权使用虚拟控制台", 403);
    }

    private async Task<ServiceResult<ConsoleTarget>> ValidateOperationTargetAsync(ConsoleTarget target, long userId, long authVersion, CancellationToken ct)
    {
        var current = await targets.ResolveAsync(target.ServerId, userId, ct);
        if (!current.Success) return current;
        if (current.Data!.AuthVersion != authVersion)
            return Fail<ConsoleTarget>("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        return ConsoleTargetIdentity.Create(current.Data) == ConsoleTargetIdentity.Create(target) ? current
            : Fail<ConsoleTarget>("MEDIA_TARGET_CHANGED", "服务器管理目标已变更，本次介质操作未发送", 409);
    }

    public async Task<ServiceResult<PageData<VirtualMediaImageDto>>> ListImagesAsync(int current, int pageSize, CancellationToken ct)
    {
        await _storeGate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            if (!_healthy) return Fail<PageData<VirtualMediaImageDto>>("MEDIA_STORE_UNAVAILABLE", "镜像记录不可用，请检查独立存储", 503);
            current = Math.Max(1, current); pageSize = Math.Clamp(pageSize, 1, 100);
            var rows = _images.Values.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
            return ServiceResult<PageData<VirtualMediaImageDto>>.Ok(new(rows.Skip((current - 1) * pageSize).Take(pageSize).ToArray(), rows.Length, current, pageSize));
        }
        finally { _storeGate.Release(); }
    }

    public async Task<ServiceResult<VirtualMediaImageDto>> UploadAsync(long userId, long authVersion, string fileName, long length, Stream content, CancellationToken ct)
    {
        var authorization = await AuthorizeAsync(userId, authVersion, ct);
        if (!authorization.Success) return Fail<VirtualMediaImageDto>(authorization.ErrorCode!, authorization.ErrorMessage!, authorization.StatusCode);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200 || fileName.Any(char.IsControl)
            || fileName.IndexOfAny(['/', '\\', ':']) >= 0)
            return Fail<VirtualMediaImageDto>("MEDIA_FILE_NAME_INVALID", "请输入不含路径的镜像文件名");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".iso" or ".img")) return Fail<VirtualMediaImageDto>("MEDIA_TYPE_UNSUPPORTED", "仅支持 ISO 和 1.44 MB IMG 镜像");
        if (length <= 0) return Fail<VirtualMediaImageDto>("MEDIA_LENGTH_REQUIRED", "上传必须声明有效文件大小", 411);
        if (length > options.MaximumImageBytes) return Fail<VirtualMediaImageDto>("MEDIA_TOO_LARGE", "镜像超过单文件容量限制", 413);
        if (extension == ".img" && length != 1_474_560) return Fail<VirtualMediaImageDto>("MEDIA_IMG_SIZE_UNSUPPORTED", "当前只支持 1.44 MB 软盘 IMG 镜像");
        var id = ids.NextId().ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _storeGate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            if (!_healthy) return Fail<VirtualMediaImageDto>("MEDIA_STORE_UNAVAILABLE", "镜像记录不可用，请检查独立存储", 503);
            var stored = Directory.EnumerateFiles(options.StorageDirectory).Sum(x => new FileInfo(x).Length);
            if (_images.Count + _activeUploads >= options.MaximumImages || stored + _reservedBytes + length > options.MaximumLibraryBytes)
                return Fail<VirtualMediaImageDto>("MEDIA_STORAGE_FULL", "镜像存储容量或数量已达到限制", 409);
            _reservedBytes += length; _activeUploads++;
        }
        finally { _storeGate.Release(); }
        try
        {
            var path = Path.Combine(options.StorageDirectory, id + extension);
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var prefix = new MemoryStream();
            long total = 0;
            await using (var file = new FileStream(path, fileOptions))
            {
                var buffer = new byte[128 * 1024];
                int count;
                while ((count = await content.ReadAsync(buffer, ct)) > 0)
                {
                    total += count;
                    if (total > length) return Fail<VirtualMediaImageDto>("MEDIA_LENGTH_MISMATCH", "实际上传内容超过声明大小");
                    if (prefix.Length < 128 * 1024) prefix.Write(buffer, 0, (int)Math.Min(count, 128 * 1024 - prefix.Length));
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            if (total != length) return Fail<VirtualMediaImageDto>("MEDIA_LENGTH_MISMATCH", "上传内容不完整，请重新选择文件");
            if (extension == ".iso" && !IsIso(prefix.ToArray())) return Fail<VirtualMediaImageDto>("MEDIA_ISO_INVALID", "未识别到 ISO9660 或 UDF 光盘镜像标识");
            var image = new VirtualMediaImageDto(id, fileName, extension == ".iso" ? "CD" : "Floppy", length,
                Convert.ToHexString(hash.GetHashAndReset()), DateTimeOffset.UtcNow);
            await _storeGate.WaitAsync(ct);
            try
            {
                // 大文件传输期间可能被撤权；未授权完成的文件不登记为可挂载镜像，保留文件供核对。
                authorization = await AuthorizeAsync(userId, authVersion, ct);
                if (!authorization.Success) return Fail<VirtualMediaImageDto>(authorization.ErrorCode!, authorization.ErrorMessage!, authorization.StatusCode);
                await AppendAsync(new JournalEntry(image, null), ct); _images.Add(id, image);
            }
            finally { _storeGate.Release(); }
            return ServiceResult<VirtualMediaImageDto>.Ok(image);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (IOException) { return Fail<VirtualMediaImageDto>("MEDIA_UPLOAD_FAILED", "镜像写入失败；不完整文件保留，不会参与挂载", 503); }
        finally
        {
            await _storeGate.WaitAsync(CancellationToken.None);
            try { _reservedBytes -= length; _activeUploads--; }
            finally { _storeGate.Release(); }
        }
    }

    public async Task<ServiceResult<VirtualMediaStatusDto>> StatusAsync(long serverId, long userId, CancellationToken ct)
    {
        var target = await targets.ResolveAsync(serverId, userId, ct);
        if (!target.Success) return Fail<VirtualMediaStatusDto>(target.ErrorCode!, target.ErrorMessage!, target.StatusCode);
        var gate = _serverGates.GetOrAdd(serverId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var driver = Driver(target.Data!);
            if (driver is null) return ServiceResult<VirtualMediaStatusDto>.Ok(new(serverId.ToString(), false, false, PublicBase() is not null, [], [], ["此控制器未配置虚拟介质驱动"]));
            var read = await driver.ReadAsync(target.Data!.Connection, ct);
            if (!read.Success) return Fail<VirtualMediaStatusDto>(read.ErrorCode!, read.ErrorMessage!, read.StatusCode);
            await _storeGate.WaitAsync(ct);
            try
            {
                await LoadAsync(ct);
                if (!_healthy) return Fail<VirtualMediaStatusDto>("MEDIA_STORE_UNAVAILABLE", "介质意图记录不可用，已阻断挂载与卸载", 503);
                await ReconcileAsync(target.Data!, read.Data!, ct);
                var targetBlocked = HasForeignOutstanding(target.Data!);
                var slots = read.Data!.Select(slot =>
                {
                    var own = Owned(target.Data!, slot);
                    return new VirtualMediaSlotDto(slot.Id, slot.Name, slot.MediaType, slot.Inserted, slot.WriteProtected,
                        SafeName(slot.ImageUri), own?.Operation.ImageId, !targetBlocked && CanInsert(slot), !targetBlocked && own is not null && slot.Inserted && slot.EjectTarget is not null);
                }).ToArray();
                var warnings = new List<string>();
                if (PublicBase() is null) warnings.Add("本地 ISO 可通过控制台直接映射；平台镜像库挂载需部署时配置 BMC 可达的 PublicBaseUrl，URL 挂载不受此限制");
                if (read.Data!.Any(s => s.Inserted && Owned(target.Data!, s) is null)) warnings.Add("设备存在其他客户端或历史介质连接，本平台不会覆盖或自动卸载");
                if (targetBlocked) warnings.Add("服务器管理目标已变更或存在未绑定目标的历史意图；请恢复原目标核对未结束的介质操作，当前目标的写操作已阻断");
                var operations = _operations.Values.Where(x => x.UserId == userId && x.Operation.ServerId == serverId.ToString())
                    .OrderByDescending(x => x.Operation.CreatedAt).Take(20).Select(x => ViewOperation(x, target.Data!, userId)).ToArray();
                return ServiceResult<VirtualMediaStatusDto>.Ok(new(serverId.ToString(), slots.Length > 0, slots.Any(s => s.CanMount), PublicBase() is not null, slots, operations, warnings));
            }
            finally { _storeGate.Release(); }
        }
        finally { gate.Release(); }
    }

    public Task<ServiceResult<VirtualMediaOperationDto>> MountAsync(long serverId, long userId, long authVersion, MountVirtualMediaRequest request, CancellationToken ct)
        => ExecuteAsync(serverId, userId, authVersion, request.SlotId, request.Confirmation, request.RequestId, request.ImageId, request.ImageUrl, false, ct);

    public Task<ServiceResult<VirtualMediaOperationDto>> EjectAsync(long serverId, long userId, long authVersion, EjectVirtualMediaRequest request, CancellationToken ct)
        => ExecuteAsync(serverId, userId, authVersion, request.SlotId, request.Confirmation, request.RequestId, null, null, true, ct);

    private async Task<ServiceResult<VirtualMediaOperationDto>> ExecuteAsync(long serverId, long userId, long authVersion, string slotId,
        string confirmation, string requestId, string? imageId, string? imageUrl, bool eject, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 100 || requestId.Any(char.IsControl))
            return Fail<VirtualMediaOperationDto>("MEDIA_REQUEST_ID_INVALID", "请提供有效的操作请求标识");
        if (!eject && string.IsNullOrWhiteSpace(imageId) == string.IsNullOrWhiteSpace(imageUrl))
            return Fail<VirtualMediaOperationDto>("MEDIA_SOURCE_REQUIRED", "请选择平台镜像或填写 URL，两者只能选择其一");
        if (imageUrl is not null && !ValidImageUrl(imageUrl)) return Fail<VirtualMediaOperationDto>("MEDIA_URL_INVALID", "镜像 URL 必须是无账号密码的 HTTP/HTTPS ISO 或 IMG 地址");
        var resolved = await targets.ResolveAsync(serverId, userId, ct);
        if (!resolved.Success) return Fail<VirtualMediaOperationDto>(resolved.ErrorCode!, resolved.ErrorMessage!, resolved.StatusCode);
        var target = resolved.Data!;
        if (target.AuthVersion != authVersion) return Fail<VirtualMediaOperationDto>("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        if (confirmation != target.ServerName) return Fail<VirtualMediaOperationDto>("CONFIRMATION_REQUIRED", "请输入完整服务器名称确认介质操作");
        var driver = Driver(target);
        if (driver is null) return Fail<VirtualMediaOperationDto>("MEDIA_UNSUPPORTED", "此控制器尚不支持虚拟介质操作", 409);
        var gate = _serverGates.GetOrAdd(serverId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var key = Key(serverId, userId, requestId);
            var requestHash = Hash($"{(eject ? "Eject" : "Mount")}|{slotId}|{imageId}|{imageUrl}");
            await _storeGate.WaitAsync(ct);
            try
            {
                await LoadAsync(ct);
                if (!_healthy) return Fail<VirtualMediaOperationDto>("MEDIA_STORE_UNAVAILABLE", "介质意图记录不可用，已阻断写操作", 503);
                if (_operations.TryGetValue(key, out var prior))
                {
                    if (!SameTarget(prior, target)) return Fail<VirtualMediaOperationDto>("MEDIA_TARGET_CHANGED", "原请求绑定的管理目标与当前服务器不同，不能跨设备复用请求", 409);
                    return prior.RequestHash == requestHash ? ServiceResult<VirtualMediaOperationDto>.Ok(ViewOperation(prior, target, userId))
                        : Fail<VirtualMediaOperationDto>("MEDIA_REQUEST_CONFLICT", "同一请求标识不能用于不同介质操作", 409);
                }
                if (HasForeignOutstanding(target))
                    return Fail<VirtualMediaOperationDto>("MEDIA_TARGET_CHANGED", "原管理目标存在未结束的介质意图，请恢复原目标核对；不会向当前目标发送动作", 409);
                if (_operations.Values.Any(x => x.Operation.ServerId == serverId.ToString() && x.Operation.Status is "Submitted" or "Unknown"))
                    return Fail<VirtualMediaOperationDto>("MEDIA_RESULT_PENDING", "该服务器存在待核对的介质操作，请先刷新原操作状态", 409);
            }
            finally { _storeGate.Release(); }
            var state = await driver.ReadAsync(target.Connection, ct);
            if (!state.Success) return Fail<VirtualMediaOperationDto>(state.ErrorCode!, state.ErrorMessage!, state.StatusCode);
            var current = await ValidateOperationTargetAsync(target, userId, authVersion, ct);
            if (!current.Success) return Fail<VirtualMediaOperationDto>(current.ErrorCode!, current.ErrorMessage!, current.StatusCode);
            var slot = state.Data!.SingleOrDefault(s => s.Id == slotId);
            if (slot is null) return Fail<VirtualMediaOperationDto>("MEDIA_SLOT_NOT_FOUND", "设备介质槽不存在", 404);
            string? accessHash = null;
            Uri? source = null;
            JournalOperation record;
            await _storeGate.WaitAsync(ct);
            try
            {
                if (eject)
                {
                    var own = Owned(target, slot);
                    if (own is null || !slot.Inserted || slot.EjectTarget is null)
                        return Fail<VirtualMediaOperationDto>("MEDIA_NOT_OWNED", "仅允许卸载本平台确认拥有的挂载，不能拔出其他客户端介质", 409);
                    source = new Uri(_protector.Unprotect(own.ProtectedSource));
                    imageId = own.Operation.ImageId;
                }
                else
                {
                    if (!CanInsert(slot)) return Fail<VirtualMediaOperationDto>("MEDIA_SLOT_BUSY", "介质槽已占用或未确认支持只读挂载", 409);
                    VirtualMediaImageDto? image = null;
                    if (imageId is not null)
                    {
                        if (!_images.TryGetValue(imageId, out image)) return Fail<VirtualMediaOperationDto>("MEDIA_IMAGE_NOT_FOUND", "平台镜像不存在", 404);
                        if (PublicBase() is not { } baseUri) return Fail<VirtualMediaOperationDto>("MEDIA_BASE_URL_REQUIRED", "平台镜像库挂载需先配置 BMC 可达的镜像服务地址；也可以选择本地直接映射或 URL");
                        var accessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                        accessHash = Hash(accessKey);
                        source = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/api/oob/media-content/" + accessKey + "/image" + (image.MediaType == "CD" ? ".iso" : ".img"));
                    }
                    else source = new Uri(imageUrl!);
                    var type = image?.MediaType ?? (Path.GetExtension(source.AbsolutePath).Equals(".iso", StringComparison.OrdinalIgnoreCase) ? "CD" : "Floppy");
                    if (slot.MediaType != type) return Fail<VirtualMediaOperationDto>("MEDIA_TYPE_MISMATCH", "镜像类型与设备介质槽不匹配");
                }
                var now = DateTimeOffset.UtcNow;
                var operation = new VirtualMediaOperationDto(ids.NextId().ToString(), serverId.ToString(), slotId, imageId,
                    eject ? "Eject" : "Mount", "Submitted", requestId, "操作已记录，设备结果待确认", now, now);
                record = new(operation, userId, requestHash, _protector.Protect(source.AbsoluteUri), accessHash, false, ConsoleTargetIdentity.Create(target));
                await AppendAsync(new(null, record), ct);
                _operations[key] = record;
            }
            finally { _storeGate.Release(); }
            ServiceResult<string> result;
            var dispatch = await ValidateOperationTargetAsync(target, userId, authVersion, ct);
            try { result = !dispatch.Success ? ServiceResult<string>.Fail(dispatch.ErrorCode!, dispatch.ErrorMessage!, dispatch.StatusCode)
                : eject ? await driver.EjectAsync(target.Connection, slot, ct) : await driver.MountAsync(target.Connection, slot, source!, ct); }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException)
            { result = ServiceResult<string>.Ok("Unknown"); }
            var final = record with { Operation = record.Operation with
            {
                Status = result.Success ? result.Data! : "Rejected",
                Message = result.Success ? result.Data == "Unknown" ? "设备结果未知，请查询原操作；不会自动重发" : "设备已接受操作，等待状态核对" : result.ErrorMessage,
                UpdatedAt = DateTimeOffset.UtcNow
            }};
            // 即使浏览器取消，也必须保存发送结果；后续查询依据原请求标识核对。
            await _storeGate.WaitAsync(CancellationToken.None);
            try { await AppendAsync(new(null, final), CancellationToken.None); _operations[key] = final; }
            finally { _storeGate.Release(); }
            if (!dispatch.Success) return Fail<VirtualMediaOperationDto>(dispatch.ErrorCode!, dispatch.ErrorMessage!, dispatch.StatusCode);
            return ServiceResult<VirtualMediaOperationDto>.Ok(ViewOperation(final, target, userId));
        }
        finally { gate.Release(); }
    }

    public async Task<ServiceResult<VirtualMediaOperationDto>> OperationAsync(long serverId, long userId, string requestId, CancellationToken ct)
    {
        var status = await StatusAsync(serverId, userId, ct);
        if (!status.Success) return Fail<VirtualMediaOperationDto>(status.ErrorCode!, status.ErrorMessage!, status.StatusCode);
        var current = await targets.ResolveAsync(serverId, userId, ct);
        if (!current.Success) return Fail<VirtualMediaOperationDto>(current.ErrorCode!, current.ErrorMessage!, current.StatusCode);
        await _storeGate.WaitAsync(ct);
        try
        {
            if (!_operations.TryGetValue(Key(serverId, userId, requestId), out var operation)) return Fail<VirtualMediaOperationDto>("MEDIA_OPERATION_NOT_FOUND", "介质操作不存在", 404);
            return SameTarget(operation, current.Data!) ? ServiceResult<VirtualMediaOperationDto>.Ok(ViewOperation(operation, current.Data!, userId))
                : Fail<VirtualMediaOperationDto>("MEDIA_TARGET_CHANGED", "原操作绑定的管理目标与当前服务器不同，仅可作为历史记录查看", 409);
        }
        finally { _storeGate.Release(); }
    }

    public async Task<ServiceResult<VirtualMediaTrafficDto>> TrafficAsync(long serverId, long userId, CancellationToken ct)
    {
        var resolved = await targets.ResolveAsync(serverId, userId, ct);
        if (!resolved.Success) return Fail<VirtualMediaTrafficDto>(resolved.ErrorCode!, resolved.ErrorMessage!, resolved.StatusCode);
        return ServiceResult<VirtualMediaTrafficDto>.Ok(_traffic.GetOrAdd(serverId, _ => new()).Snapshot());
    }

    public async Task<ServiceResult<VirtualMediaOperationDto>> ReviewNotExecutedAsync(long serverId, long userId, long authVersion, string requestId, ReviewVirtualMediaRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 500)
            return Fail<VirtualMediaOperationDto>("MEDIA_REVIEW_NOTE_REQUIRED", "请填写不超过500字的人工核对依据");
        var resolved = await targets.ResolveAsync(serverId, userId, ct);
        if (!resolved.Success) return Fail<VirtualMediaOperationDto>(resolved.ErrorCode!, resolved.ErrorMessage!, resolved.StatusCode);
        if (resolved.Data!.AuthVersion != authVersion) return Fail<VirtualMediaOperationDto>("UNAUTHORIZED", "登录已失效，请重新登录", 401);
        if (request.Confirmation != resolved.Data!.ServerName) return Fail<VirtualMediaOperationDto>("CONFIRMATION_REQUIRED", "请输入完整服务器名称确认人工核对");
        var driver = Driver(resolved.Data);
        if (driver is null) return Fail<VirtualMediaOperationDto>("MEDIA_UNSUPPORTED", "此控制器不支持介质状态核对", 409);
        var gate = _serverGates.GetOrAdd(serverId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var state = await driver.ReadAsync(resolved.Data.Connection, ct);
            if (!state.Success) return Fail<VirtualMediaOperationDto>(state.ErrorCode!, state.ErrorMessage!, state.StatusCode);
            await _storeGate.WaitAsync(ct);
            try
            {
                var current = await ValidateOperationTargetAsync(resolved.Data, userId, authVersion, ct);
                if (!current.Success) return Fail<VirtualMediaOperationDto>(current.ErrorCode!, current.ErrorMessage!, current.StatusCode);
                await LoadAsync(ct);
                if (!_healthy) return Fail<VirtualMediaOperationDto>("MEDIA_STORE_UNAVAILABLE", "介质记录不可用，已阻断人工核对", 503);
                await ReconcileAsync(resolved.Data, state.Data!, ct);
                var key = Key(serverId, userId, requestId);
                if (!_operations.TryGetValue(key, out var record)) return Fail<VirtualMediaOperationDto>("MEDIA_OPERATION_NOT_FOUND", "原操作不存在或不属于当前操作人", 404);
                if (!SameTarget(record, resolved.Data)) return Fail<VirtualMediaOperationDto>("MEDIA_TARGET_CHANGED", "不能以当前设备状态核对原管理目标的意图，请恢复原目标后核对", 409);
                if (record.Operation.Status is not ("Submitted" or "Unknown")) return Fail<VirtualMediaOperationDto>("MEDIA_OPERATION_FINAL", "原操作已有终态，不能改写历史结论", 409);
                var slot = state.Data!.SingleOrDefault(x => x.Id == record.Operation.SlotId);
                if (slot is null || (record.Operation.Action == "Mount" ? slot.Inserted : !slot.Inserted || slot.ImageUri is null || !SourceMatches(record, slot.ImageUri)))
                    return Fail<VirtualMediaOperationDto>("MEDIA_REVIEW_STATE_CONFLICT", "设备当前状态不允许确认该动作未执行，请重新核对", 409);
                var reviewed = record with { Revoked = record.Operation.Action == "Mount" || record.Revoked,
                    Operation = record.Operation with { Status = "NotExecuted", UpdatedAt = DateTimeOffset.UtcNow,
                        Message = "原操作人已人工确认未执行：" + request.Note.Trim() } };
                await AppendAsync(new(null, reviewed), ct); _operations[key] = reviewed;
                return ServiceResult<VirtualMediaOperationDto>.Ok(ViewOperation(reviewed, resolved.Data, userId));
            }
            finally { _storeGate.Release(); }
        }
        finally { gate.Release(); }
    }

    public async Task<ServiceResult<(Stream Content, long Length, string MediaType, long ServerId)>> OpenImageAsync(string accessKey, CancellationToken ct)
    {
        if (accessKey.Length != 64 || !accessKey.All(Uri.IsHexDigit)) return Fail<(Stream, long, string, long)>("MEDIA_CONTENT_NOT_FOUND", "镜像内容不可用", 404);
        await _storeGate.WaitAsync(ct);
        try
        {
            await LoadAsync(ct);
            if (!_healthy) return Fail<(Stream, long, string, long)>("MEDIA_STORE_UNAVAILABLE", "镜像内容暂不可用", 503);
            var hash = Hash(accessKey);
            var mount = _operations.Values.FirstOrDefault(x => !x.Revoked && x.AccessHash == hash && x.Operation.Action == "Mount" && x.Operation.Status != "Rejected");
            if (mount?.Operation.ImageId is not { } imageId || !_images.TryGetValue(imageId, out var image))
                return Fail<(Stream, long, string, long)>("MEDIA_CONTENT_NOT_FOUND", "镜像内容不可用", 404);
            // BMC 无 Cookie；读取凭据仍须绑定原设备，管理地址变更不能让旧 token 跨设备继续供给。
            // 已挂载镜像独立于浏览器登录与控制台权限；仍保留原账号状态及设备绑定校验。
            var current = await targets.ResolveMediaContentAsync(long.Parse(mount.Operation.ServerId), mount.UserId, ct);
            if (!current.Success || !SameTarget(mount, current.Data!))
                return Fail<(Stream, long, string, long)>("MEDIA_CONTENT_NOT_FOUND", "镜像内容不可用", 404);
            var path = Path.Combine(options.StorageDirectory, image.Id + (image.MediaType == "CD" ? ".iso" : ".img"));
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
                if (stream.Length != image.SizeBytes) { await stream.DisposeAsync(); return Fail<(Stream, long, string, long)>("MEDIA_CONTENT_CHANGED", "镜像文件大小已改变，已停止供给", 409); }
                return ServiceResult<(Stream, long, string, long)>.Ok((stream, image.SizeBytes, "application/octet-stream", long.Parse(mount.Operation.ServerId)));
            }
            catch (IOException) { return Fail<(Stream, long, string, long)>("MEDIA_CONTENT_NOT_FOUND", "镜像文件暂不可读取", 503); }
        }
        finally { _storeGate.Release(); }
    }

    public void RecordRead(long serverId, int count) => _traffic.GetOrAdd(serverId, _ => new()).Add(count);

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (string.IsNullOrWhiteSpace(options.StorageDirectory)) { _healthy = false; return; }
            if (!Directory.Exists(options.StorageDirectory))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(options.StorageDirectory);
                else Directory.CreateDirectory(options.StorageDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // 文件意图日志只支持单实例；独占租约防止两个进程各自判断后向同一设备重复发送。
            var leaseOptions = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) leaseOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            _writerLease = new FileStream(Path.Combine(options.StorageDirectory, "store.lock"), leaseOptions);
            if (!File.Exists(JournalPath)) return;
            using var input = new StreamReader(JournalPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = await input.ReadLineAsync(ct)) is not null)
            {
                if (line.Length == 0 || line.Length > 64 * 1024) { _healthy = false; return; }
                var entry = JsonSerializer.Deserialize<JournalEntry>(line) ?? throw new JsonException();
                if (entry.Image is { } image) _images[image.Id] = image;
                if (entry.Operation is { } operation)
                {
                    // 重启不能把最后一次发出但未核对的请求当成未发送。
                    if (operation.Operation.Status == "Submitted") operation = operation with { Operation = operation.Operation with { Status = "Unknown" } };
                    _operations[Key(long.Parse(operation.Operation.ServerId), operation.UserId, operation.Operation.RequestId)] = operation;
                }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or FormatException or UnauthorizedAccessException)
        { _healthy = false; }
        catch (OperationCanceledException) { _healthy = false; throw; }
    }

    private async Task AppendAsync(JournalEntry entry, CancellationToken ct)
    {
        if (!_healthy) throw new IOException("介质意图存储不可用");
        try
        {
            var fileOptions = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var file = new FileStream(JournalPath, fileOptions);
            await file.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n"), ct);
            await file.FlushAsync(ct); file.Flush(flushToDisk: true);
        }
        catch { _healthy = false; throw; }
    }

    private static bool SameTarget(JournalOperation operation, ConsoleTarget target) => operation.TargetIdentity is { Length: 64 } identity
        && identity == ConsoleTargetIdentity.Create(target);

    private static VirtualMediaOperationDto ViewOperation(JournalOperation operation, ConsoleTarget target, long userId) => operation.Operation with
    {
        CanReviewNotExecuted = operation.UserId == userId && SameTarget(operation, target)
            && operation.Operation.Status is "Submitted" or "Unknown"
    };

    private bool HasForeignOutstanding(ConsoleTarget target) => _operations.Values.Any(operation => operation.Operation.ServerId == target.ServerId.ToString()
        && !SameTarget(operation, target) && (operation.Operation.Status is "Submitted" or "Unknown"
            || !operation.Revoked && operation.Operation.Action == "Mount" && operation.Operation.Status == "Confirmed"));

    private JournalOperation? Owned(ConsoleTarget target, VirtualMediaDeviceSlot slot) => slot.ImageUri is null ? null : _operations.Values
        .Where(x => !x.Revoked && x.Operation.Action == "Mount" && x.Operation.Status != "Rejected"
            && x.Operation.ServerId == target.ServerId.ToString() && SameTarget(x, target) && x.Operation.SlotId == slot.Id)
        .OrderByDescending(x => x.Operation.CreatedAt).FirstOrDefault(x => SourceMatches(x, slot.ImageUri));

    private bool SourceMatches(JournalOperation record, string imageUri)
    {
        try { return Uri.TryCreate(imageUri, UriKind.Absolute, out var actual) && new Uri(_protector.Unprotect(record.ProtectedSource)) == actual; }
        catch (CryptographicException) { return false; }
    }

    private async Task ReconcileAsync(ConsoleTarget target, IReadOnlyList<VirtualMediaDeviceSlot> slots, CancellationToken ct)
    {
        // 保留当时“已确认挂载”的历史事实，但外部弹出/更换后立即撤销旧镜像供给凭据。
        foreach (var pair in _operations.Where(x => !x.Value.Revoked && x.Value.Operation.ServerId == target.ServerId.ToString() && SameTarget(x.Value, target)
            && x.Value.Operation.Action == "Mount" && x.Value.Operation.Status == "Confirmed").ToArray())
        {
            var slot = slots.SingleOrDefault(x => x.Id == pair.Value.Operation.SlotId);
            if (slot is null || slot.Inserted && slot.ImageUri is not null && SourceMatches(pair.Value, slot.ImageUri)) continue;
            var revoked = pair.Value with { Revoked = true };
            await AppendAsync(new(null, revoked), ct); _operations[pair.Key] = revoked;
        }
        foreach (var pair in _operations.Where(x => x.Value.Operation.ServerId == target.ServerId.ToString() && SameTarget(x.Value, target) && x.Value.Operation.Status is "Submitted" or "Unknown").ToArray())
        {
            var record = pair.Value;
            var slot = slots.SingleOrDefault(x => x.Id == record.Operation.SlotId);
            if (slot is null) continue;
            var confirmed = record.Operation.Action == "Mount" ? slot.Inserted && slot.WriteProtected && slot.ImageUri is not null && SourceMatches(record, slot.ImageUri) : !slot.Inserted;
            if (!confirmed) continue;
            var final = record with { Operation = record.Operation with { Status = "Confirmed", Message = "设备介质状态已确认", UpdatedAt = DateTimeOffset.UtcNow } };
            await AppendAsync(new(null, final), ct); _operations[pair.Key] = final;
            if (record.Operation.Action == "Eject")
                foreach (var mount in _operations.Where(x => !x.Value.Revoked && x.Value.Operation.Action == "Mount"
                    && x.Value.Operation.ServerId == target.ServerId.ToString() && SameTarget(x.Value, target) && x.Value.Operation.SlotId == record.Operation.SlotId).ToArray())
                {
                    var revoked = mount.Value with { Revoked = true };
                    await AppendAsync(new(null, revoked), ct); _operations[mount.Key] = revoked;
                }
        }
    }

    private Uri? PublicBase() => Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 ? uri : null;
    private static bool CanInsert(VirtualMediaDeviceSlot slot) => !slot.Inserted && slot.InsertTarget is not null
        && slot.MediaType is "CD" or "Floppy" && (!slot.UsesIloOem || slot.WriteProtected);
    private static string? SafeName(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Path.GetFileName(uri.AbsolutePath) : null;
    private static bool ValidImageUrl(string value) => value.Length <= 2048 && !value.Any(char.IsControl)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && !uri.IsLoopback && uri.Host != "169.254.169.254"
        && Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".iso" or ".img";
    private static bool IsIso(byte[] bytes)
    {
        for (var offset = 16 * 2048 + 1; offset + 5 <= bytes.Length; offset += 2048)
            if (Encoding.ASCII.GetString(bytes, offset, 5) is "CD001" or "BEA01" or "NSR02" or "NSR03") return true;
        return false;
    }

    public void Dispose() => _writerLease?.Dispose(); // 只释放本进程文件句柄，不卸载介质或删除文件。

    private sealed record JournalEntry(VirtualMediaImageDto? Image, JournalOperation? Operation);
    private sealed record JournalOperation(VirtualMediaOperationDto Operation, long UserId, string RequestHash,
        string ProtectedSource, string? AccessHash, bool Revoked, string? TargetIdentity = null);
    private sealed class ReadTraffic
    {
        private readonly Queue<(long At, int Count)> _recent = new();
        private long _total;
        public void Add(int count) { lock (_recent) { _total += count; _recent.Enqueue((Stopwatch.GetTimestamp(), count)); Trim(); } }
        public VirtualMediaTrafficDto Snapshot() { lock (_recent) { Trim(); return new(_total, _recent.Sum(x => (double)x.Count), DateTimeOffset.UtcNow); } }
        private void Trim() { while (_recent.TryPeek(out var value) && Stopwatch.GetElapsedTime(value.At).TotalSeconds >= 1) _recent.Dequeue(); }
    }
}

using System.Text;
using System.Text.Json;
using OobManagement.Domain.Models;

namespace OobManagement.Infrastructure.Common;

/// <summary>单实例串行追加脱敏 JSONL；先写缓冲再入库，稳定主键使提交后崩溃的重放仍然幂等。</summary>
public sealed class ExceptionLogSpool(ExceptionLogOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextReplayAt;
    private long? _checkpoint;
    private string RecordsPath => Path.Combine(options.SpoolDirectory, "exceptions.jsonl");
    private string CheckpointPath => Path.Combine(options.SpoolDirectory, "exceptions.checkpoint");

    public async Task AppendAsync(SystemExceptionRecord record, Func<SystemExceptionRecord, CancellationToken, Task> persist)
    {
        await _gate.WaitAsync();
        try
        {
            EnsureDirectory();
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
            await using (var file = OpenPrivate(RecordsPath, FileMode.OpenOrCreate))
            {
                // 强制退出可能留下末尾半行；保留原文并隔开新记录，绝不截断或删除缓冲文件。
                if (file.Length > 0)
                {
                    file.Seek(-1, SeekOrigin.End);
                    if (file.ReadByte() != '\n') { file.Seek(0, SeekOrigin.End); file.WriteByte((byte)'\n'); }
                }
                file.Seek(0, SeekOrigin.End);
                await file.WriteAsync(bytes);
                file.Flush(flushToDisk: true);
            }
            await ReplayLockedAsync(persist, CancellationToken.None);
        }
        catch (Exception exception) { ReportFailure(exception); }
        finally { _gate.Release(); }
    }

    public async Task ReplayAsync(Func<SystemExceptionRecord, CancellationToken, Task> persist, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct);
            try { await ReplayLockedAsync(persist, ct); }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception exception) { ReportFailure(exception); }
    }

    private async Task ReplayLockedAsync(Func<SystemExceptionRecord, CancellationToken, Task> persist, CancellationToken stoppingToken)
    {
        if (DateTimeOffset.UtcNow < _nextReplayAt) return;
        try { await DrainLockedAsync(persist, stoppingToken); }
        catch
        {
            // 数据库已知不可用时先继续可靠追加，避免一批并行采样逐条等待数据库超时。
            _nextReplayAt = DateTimeOffset.UtcNow.AddSeconds(5);
            throw;
        }
    }

    private async Task DrainLockedAsync(Func<SystemExceptionRecord, CancellationToken, Task> persist, CancellationToken stoppingToken)
    {
        if (!File.Exists(RecordsPath)) return;
        if (_checkpoint is null)
        {
            var checkpoints = File.Exists(CheckpointPath) ? await File.ReadAllTextAsync(CheckpointPath, stoppingToken) : "";
            // checkpoint 也只追加完整行；强杀留下的末尾半行不能被误认成有效中间偏移。
            _checkpoint = checkpoints.Split('\n').SkipLast(1).Reverse()
                .Select(value => long.TryParse(value, out var position) ? (long?)position : null).FirstOrDefault(value => value is >= 0) ?? 0;
        }
        var offset = _checkpoint.Value;
        await using var file = new FileStream(RecordsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // 不可信/中断的 checkpoint 回退到起点，数据库主键保证不会制造重复。
        file.Position = offset >= 0 && offset <= file.Length ? offset : 0;
        offset = file.Position;
        using var reader = new StreamReader(file, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        batch.CancelAfter(options.DatabaseTimeout);
        for (var count = 0; count < 100; count++)
        {
            var line = await reader.ReadLineAsync(batch.Token);
            if (line is null) break;
            var nextOffset = offset + Encoding.UTF8.GetByteCount(line) + 1;
            // 末尾未完成一行先等待下一次追加，不把半条记录当作成功。
            if (nextOffset > file.Length) break;
            SystemExceptionRecord? record;
            try { record = JsonSerializer.Deserialize<SystemExceptionRecord>(line); }
            catch (JsonException exception) { ReportFailure(exception); record = null; }
            if (record is not null) await persist(record, batch.Token);
            // 持久化成功后才推进；半写 checkpoint 导致重放也只会命中相同主键。
            await using (var checkpoint = OpenPrivate(CheckpointPath, FileMode.OpenOrCreate))
            {
                if (checkpoint.Length > 0)
                {
                    checkpoint.Seek(-1, SeekOrigin.End);
                    if (checkpoint.ReadByte() != '\n') { checkpoint.Seek(0, SeekOrigin.End); checkpoint.WriteByte((byte)'\n'); }
                }
                checkpoint.Seek(0, SeekOrigin.End);
                await checkpoint.WriteAsync(Encoding.UTF8.GetBytes(nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"), batch.Token);
                checkpoint.Flush(flushToDisk: true);
            }
            _checkpoint = nextOffset;
            offset = nextOffset;
        }
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(options.SpoolDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(options.SpoolDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStream OpenPrivate(string path, FileMode mode)
    {
        var streamOptions = new FileStreamOptions
        {
            Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows()) streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, streamOptions);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return stream;
    }

    private static void ReportFailure(Exception exception)
    {
        // 不调用 ILogger 或 recorder，避免日志数据库自身故障递归。仅输出固定摘要与异常类型。
        try { Console.Error.WriteLine("异常缓冲或入库暂不可用，已保留可用缓冲；类型=" + exception.GetType().Name); }
        catch { /* 输出设备也可能不可用，不能再把日志故障传播至业务。 */ }
    }
}

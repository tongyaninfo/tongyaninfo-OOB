using System.Diagnostics;
using System.Text.Json;
using OobManagement.Application.Models;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Common;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>仅操作本用例仓库外新目录，验证故障缓冲不会递归、丢弃积压或阻塞整个采样批次。</summary>
public sealed class ExceptionSpoolSafetyTests
{
    [Fact]
    public async Task Timeout_backs_off_and_all_parallel_faults_remain_durable_without_recursion()
    {
        var options = Options() with { DatabaseTimeout = TimeSpan.FromMilliseconds(50) };
        var spool = new ExceptionLogSpool(options);
        var attempts = 0;
        async Task Fail(SystemExceptionRecord record, CancellationToken ct)
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(1, 12).Select(id => spool.AppendAsync(Record(id), Fail)));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "数据库已失败时不应让每个并发故障各等待一次超时");
        Assert.Equal(1, attempts);
        var path = Path.Combine(options.SpoolDirectory, "exceptions.jsonl");
        Assert.Equal(12, (await File.ReadAllLinesAsync(path)).Length);
        var saved = new HashSet<long>();
        await new ExceptionLogSpool(options with { DatabaseTimeout = TimeSpan.FromSeconds(2) }).ReplayAsync((record, _) => { saved.Add(record.Id); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(12, saved.Count);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Interrupted_last_line_is_preserved_and_new_complete_record_can_replay()
    {
        var options = Options();
        Directory.CreateDirectory(options.SpoolDirectory);
        var path = Path.Combine(options.SpoolDirectory, "exceptions.jsonl");
        await File.WriteAllTextAsync(path, "{\"Id\":");
        var saved = new List<long>();
        await new ExceptionLogSpool(options).AppendAsync(Record(42), (record, _) => { saved.Add(record.Id); return Task.CompletedTask; });
        Assert.Equal([42L], saved);
        Assert.StartsWith("{\"Id\":\n", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Sanitizer_excludes_native_messages_Data_and_browser_URLs_and_queries()
    {
        var native = new InvalidOperationException("password=secret-sentinel", new IOException("token=secret-sentinel"));
        native.Data["key"] = "secret-sentinel";
        var safe = ExceptionSanitizer.Create(1, native, new() { Message = "安全测试摘要", RequestPath = "/api/example?password=secret-sentinel" });
        var json = JsonSerializer.Serialize(safe);
        Assert.DoesNotContain("secret-sentinel", json);
        var client = ExceptionSanitizer.Create(2, null, new() { Kind = "ClientException", ClientExceptionType = "TypeError", Message = "secret-sentinel",
            ClientStackTrace = "TypeError: secret-sentinel\n at render (https://user:secret-sentinel@host/private/app.js:10:20?token=secret-sentinel)" });
        Assert.Equal("at app.js:10:20", client.StackTrace);
        Assert.DoesNotContain("secret-sentinel", JsonSerializer.Serialize(client));
    }

    private static ExceptionLogOptions Options() => new() { SpoolDirectory = Path.Combine(Path.GetTempPath(), "oob-exception-tests", "safety-" + Guid.NewGuid().ToString("N")) };
    private static SystemExceptionRecord Record(long id) => ExceptionSanitizer.Create(id, null, new ExceptionLogContext { Message = "受控缓冲测试" });
}

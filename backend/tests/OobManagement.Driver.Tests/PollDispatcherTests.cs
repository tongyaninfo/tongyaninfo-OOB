using OobManagement.Infrastructure.Common;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>慢设备持续运行时，完成的槽位仍能为其它设备服务；同机不重叠。</summary>
public sealed class PollDispatcherTests
{
    [Fact]
    public async Task Completed_device_releases_capacity_without_waiting_for_slow_device()
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new PollDispatcher(2);
        Assert.True(dispatcher.TryStart(1, () => slow.Task));
        Assert.True(dispatcher.TryStart(2, () => fast.Task));
        Assert.False(dispatcher.TryStart(1, () => Task.CompletedTask));
        Assert.False(dispatcher.TryStart(3, () => Task.CompletedTask));
        fast.SetResult();
        await dispatcher.ReapCompletedAsync();
        Assert.False(slow.Task.IsCompleted);
        Assert.True(dispatcher.TryStart(3, () => Task.CompletedTask));
        Assert.Equal(0, dispatcher.AvailableSlots);
        slow.SetResult();
        await dispatcher.DrainAsync();
        await dispatcher.ReapCompletedAsync();
        Assert.Equal(2, dispatcher.AvailableSlots);
    }

    [Fact]
    public async Task Failed_oldest_devices_cannot_starve_fifth_healthy_device_when_database_schedule_is_unchanged()
    {
        var now = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        var dispatcher = new PollDispatcher(4);
        var due = new long[] { 1, 2, 3, 4, 5 };
        // 模拟四台最旧设备的保存失败：持久化到期顺序不变，但内存退避仍可保证下一台获得容量。
        foreach (var id in due.Take(4))
            Assert.True(dispatcher.TryStart(id, () => { dispatcher.Defer(id, now.AddSeconds(5)); return Task.CompletedTask; }));
        await dispatcher.ReapCompletedAsync();
        var eligible = due.Except(dispatcher.UnavailableIds(now.AddSeconds(1))).Take(dispatcher.AvailableSlots).ToArray();
        Assert.Equal(new long[] { 5 }, eligible);
        Assert.True(dispatcher.TryStart(eligible[0], () => Task.CompletedTask));
        await dispatcher.ReapCompletedAsync();
        Assert.Empty(dispatcher.UnavailableIds(now.AddSeconds(5)));
    }
}

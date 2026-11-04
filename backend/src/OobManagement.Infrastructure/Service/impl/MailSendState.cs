using System.Collections.Concurrent;

namespace OobManagement.Infrastructure.Service.impl;

/// <summary>只标识当前进程正在执行的发送；不承担持久队列，重启后遗留Sending均按Unknown展示。</summary>
public sealed class MailSendState
{
    private readonly ConcurrentDictionary<long, byte> _active = new();
    public bool IsActive(long id) => _active.ContainsKey(id);
    public long[] ActiveIds => _active.Keys.ToArray();
    public void Start(long id) => _active.TryAdd(id, 0);
    public void Finish(long id) => _active.TryRemove(id, out _);
}

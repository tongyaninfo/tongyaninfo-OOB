namespace OobManagement.Desktop.Storage.Common;

/// <summary>SQLite 用 UTC 微秒整数比较/排序，与服务端 PostgreSQL 的微秒精度一致。</summary>
public static class SqliteUtcTime
{
    private static readonly long EpochMicroseconds = DateTimeOffset.UnixEpoch.UtcTicks / 10;
    public static long ToMicroseconds(DateTimeOffset value) => value.UtcTicks / 10 - EpochMicroseconds;
    public static DateTimeOffset FromMicroseconds(long value) => new(checked((value + EpochMicroseconds) * 10), TimeSpan.Zero);
}

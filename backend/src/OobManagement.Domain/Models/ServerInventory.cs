namespace OobManagement.Domain.Models;

/// <summary>库存按采集时间保存版本，失败刷新不删除上一份成功快照。</summary>
public sealed class ServerInventory
{
    public long Id { get; set; }
    public long ServerId { get; set; }
    public DateTimeOffset CollectedAt { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

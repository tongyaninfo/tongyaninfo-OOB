namespace OobManagement.Application.Models;

/// <summary>仅统计 BMC 实际从平台读取的响应字节，不代表客户机安装进度或网卡总流量。</summary>
public sealed record VirtualMediaTrafficDto(long BytesRead, double BytesPerSecond, DateTimeOffset ObservedAt);

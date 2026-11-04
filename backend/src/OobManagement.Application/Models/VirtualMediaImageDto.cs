namespace OobManagement.Application.Models;

/// <summary>镜像只包含可公开的元信息，不返回磁盘路径或设备读取凭据。</summary>
public sealed record VirtualMediaImageDto(string Id, string FileName, string MediaType, long SizeBytes,
    string Sha256, DateTimeOffset CreatedAt);

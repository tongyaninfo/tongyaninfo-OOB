namespace OobManagement.Infrastructure.Common;

/// <summary>仅配置本平台镜像供给地址及容量；不在此保存 BMC 账号或网络共享密码。</summary>
public sealed class VirtualMediaOptions
{
    public string StorageDirectory { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public long MaximumImageBytes { get; set; } = 16L * 1024 * 1024 * 1024;
    public long MaximumLibraryBytes { get; set; } = 64L * 1024 * 1024 * 1024;
    public int MaximumImages { get; set; } = 128;
}

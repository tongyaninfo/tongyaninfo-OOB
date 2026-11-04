namespace OobManagement.Infrastructure.Service.impl.OutOfBand;

/// <summary>认证生命周期只能使用标准 Sessions 集合及单个 item，不能与设备动作写路径混用。</summary>
internal static class AuthenticationSessionPaths
{
    private static readonly string[] Collections = ["/redfish/v1/Sessions", "/redfish/v1/SessionService/Sessions"];
    public static bool IsCollection(Uri target) => target.Query.Length == 0 && target.Fragment.Length == 0
        && Collections.Any(collection => target.AbsolutePath.TrimEnd('/').Equals(collection, StringComparison.OrdinalIgnoreCase));
    public static bool IsItem(Uri target)
    {
        if (target.Query.Length != 0 || target.Fragment.Length != 0) return false;
        foreach (var collection in Collections)
        {
            var prefix = collection + "/";
            if (!target.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var item = Uri.UnescapeDataString(target.AbsolutePath[prefix.Length..].TrimEnd('/'));
            if (item.Length > 0 && !item.Contains('/') && !item.Contains('\\') && item is not "." and not "..") return true;
        }
        return false;
    }
}

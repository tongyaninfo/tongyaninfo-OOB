using System.Reflection;

namespace OobManagement.Desktop.Host.Common;

/// <summary>启动和维护页面随宿主内置，首次安装无需在线资源或开发服务器。</summary>
public static class DesktopPages
{
    public static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"OobManagement.Desktop.Host.Resources.{name}.html")
            ?? throw new InvalidOperationException("桌面页面资源缺失");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

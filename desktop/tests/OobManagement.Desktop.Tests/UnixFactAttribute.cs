using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>仅符号链接回归依赖Unix权限语义，Windows不因此跳过其余SQLite和宿主测试。</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "本用例验证Unix目录符号链接；其余桌面测试照常执行。";
    }
}

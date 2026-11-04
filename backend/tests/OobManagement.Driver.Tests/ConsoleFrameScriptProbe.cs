using System.Diagnostics;
using System.Text;
using OobManagement.Application.Models;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>真实交付 HTML 经标准输入交给隔离 Node 替身；不落文件、不连接设备。</summary>
internal static class ConsoleFrameScriptProbe
{
    public static async Task AssertAsync(string vendor, ConsoleResource resource)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "console-frame-probe.mjs");
        Assert.True(File.Exists(fixture), "控制台离线脚本回归夹具未复制到测试输出");
        var start = new ProcessStartInfo("node")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(fixture);
        start.ArgumentList.Add(vendor);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(Encoding.UTF8.GetString(resource.Content));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            // 仅终止本测试刚启动的 Node 子进程，不接触开发服务或其它进程。
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            Assert.Fail("控制台 frame 离线回归超过15秒");
        }
        Assert.True(process.ExitCode == 0, $"{vendor} frame 脚本回归失败：{await error}\n{await output}");
    }
}

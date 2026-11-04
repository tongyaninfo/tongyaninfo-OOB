using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using OobManagement.Desktop.Host;
using Xunit;

namespace OobManagement.Desktop.Tests;

/// <summary>仅管理自己启动的真实宿主进程，标准输入承载本次握手，结束后保留独占数据目录。</summary>
public sealed class DesktopHostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    private readonly string _launchSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public string DirectoryPath { get; }
    public Uri Url { get; private set; } = null!;
    public bool Initialized { get; private set; }
    public int Id => _process.Id;

    private DesktopHostProcess(string directory)
    {
        DirectoryPath = directory;
        var assemblyPath = typeof(DesktopProgram).Assembly.Location;
        var testAssembly = typeof(DesktopHostProcess).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(testAssembly)!, CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--desktop-data");
        start.ArgumentList.Add(directory);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["OOB_DESKTOP_DISABLE_COLLECTOR"] = "1";
        _process = Process.Start(start) ?? throw new InvalidOperationException("测试宿主进程未启动");
        _process.StandardInput.AutoFlush = true;
        _errors = _process.StandardError.ReadToEndAsync();
    }

    public static async Task<DesktopHostProcess> StartAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var host = new DesktopHostProcess(directory);
        try
        {
            var instanceId = Guid.NewGuid().ToString();
            await host._process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { command = "start", launchSecret = host._launchSecret, instanceId }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (await host._process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var message = JsonDocument.Parse(line);
                if (!message.RootElement.TryGetProperty("type", out var type) || type.GetString() != "ready") continue;
                Assert.Equal(instanceId, message.RootElement.GetProperty("instanceId").GetString());
                host.Url = new Uri(message.RootElement.GetProperty("url").GetString()!);
                Assert.True(host.Url.IsLoopback);
                host.Initialized = message.RootElement.GetProperty("initialized").GetBoolean();
                return host;
            }
            throw new InvalidOperationException("测试宿主未返回ready：" + (await host._errors).Replace(host._launchSecret, "[redacted]", StringComparison.Ordinal));
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public HttpClient CreateClient(bool withGate = true, IEnumerable<string>? sessionCookies = null)
    {
        var cookies = new CookieContainer();
        if (withGate) cookies.Add(Url, new Cookie("OobDesktop.Gate", _launchSecret, "/") { HttpOnly = true });
        if (sessionCookies is not null)
            foreach (var value in sessionCookies) cookies.SetCookies(Url, value);
        return new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = Url };
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try { await _process.StandardInput.WriteLineAsync("{\"command\":\"shutdown\"}"); _process.StandardInput.Close(); }
            catch (IOException) { }
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException)
            {
                // 只回收本对象启动并持有句柄的进程树，不按进程名称停止其他应用。
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        await _errors;
        _process.Dispose();
    }
}

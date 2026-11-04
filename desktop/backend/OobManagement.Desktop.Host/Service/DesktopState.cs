using Microsoft.EntityFrameworkCore;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Desktop.Host.Service;

/// <summary>只有显式初始化或升级成功后才开放业务和后台采集。</summary>
public sealed class DesktopState(string directory)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string DirectoryPath { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    public string DatabasePath => Path.Combine(DirectoryPath, "data", "oob.sqlite");
    public bool Initialized { get; private set; }
    public Uri? MediaOrigin { get; set; }
    public HashSet<string> MediaAllowedAddresses { get; } = new(StringComparer.Ordinal);
    public bool HasDatabase => File.Exists(DatabasePath);
    public SemaphoreSlim MaintenanceGate { get; } = new(1, 1);
    public Task WaitAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    public async Task ProbeAsync(IServiceProvider services)
    {
        if (!HasDatabase) return;
        await using var db = await services.GetRequiredService<IDbContextFactory<OobDbContext>>().CreateDbContextAsync();
        if (!(await db.Database.GetPendingMigrationsAsync()).Any() && await db.Users.AnyAsync()) MarkReady();
    }

    public void MarkReady()
    {
        Initialized = true;
        _ready.TrySetResult();
    }
}

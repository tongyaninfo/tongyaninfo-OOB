using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OobManagement.Infrastructure.Persistence;

/// <summary>迁移生成不启动宿主或访问设备，连接仅从获准的本机文件读取。</summary>
public sealed class OobDesignTimeFactory : IDesignTimeDbContextFactory<OobDbContext>
{
    public OobDbContext CreateDbContext(string[] args)
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        var file = Path.Combine(root.FullName, ".local/oob/database-connections.json");
        var connection = "Host=127.0.0.1;Port=55432;Database=oob_management_dev;Username=placeholder;Password=placeholder";
        if (File.Exists(file))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            connection = document.RootElement.GetProperty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Testing" ? "Testing" : "Development").GetString()!;
        }
        return new(new DbContextOptionsBuilder<OobDbContext>().UseNpgsql(connection).Options);
    }
}

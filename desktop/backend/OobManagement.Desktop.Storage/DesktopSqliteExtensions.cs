using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Desktop.Storage;

/// <summary>桌面宿主在共享服务注册完成后替换工厂，服务端默认 PostgreSQL 注册保持原样。</summary>
public static class DesktopSqliteExtensions
{
    public static IServiceCollection AddDesktopSqlite(this IServiceCollection services, string databasePath)
    {
        services.RemoveAll<IDbContextFactory<OobDbContext>>();
        services.RemoveAll<OobDbContext>();
        services.RemoveAll<DbContextOptions<OobDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<OobDbContext>>();
        services.AddSingleton<IDbContextFactory<OobDbContext>>(new SqliteOobDbContextFactory(databasePath));
        services.AddScoped(provider => provider.GetRequiredService<IDbContextFactory<OobDbContext>>().CreateDbContext());
        return services;
    }
}

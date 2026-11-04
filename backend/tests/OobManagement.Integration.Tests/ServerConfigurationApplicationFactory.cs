using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;

namespace OobManagement.Integration.Tests;

/// <summary>复用真实 Testing PostgreSQL，只向本夹具暴露自己的服务器，避免导出共享测试库中的其他配置及凭据。</summary>
public sealed class ServerConfigurationApplicationFactory : OobApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDbContextFactory<OobDbContext>>();
            services.AddSingleton<IDbContextFactory<OobDbContext>>(provider =>
                new ConfigurationDbContextFactory(provider.GetRequiredService<DbContextOptions<OobDbContext>>(), RunPrefix + "-server-"));
        });
    }

    private sealed class ConfigurationDbContextFactory(DbContextOptions<OobDbContext> options, string serverPrefix) : IDbContextFactory<OobDbContext>
    {
        public OobDbContext CreateDbContext() => new ConfigurationDbContext(options, serverPrefix);
        public Task<OobDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    private sealed class ConfigurationDbContext(DbContextOptions<OobDbContext> options, string serverPrefix) : OobDbContext(options)
    {
        // 保留为上下文实例属性，让 EF 参数化前缀；模型缓存不得固定为第一次运行的夹具前缀。
        public string ServerPrefix { get; } = serverPrefix;

        protected override void OnModelCreating(ModelBuilder model)
        {
            base.OnModelCreating(model);
            model.Entity<ManagedServer>().HasQueryFilter(server => server.Name.StartsWith(ServerPrefix));
        }
    }
}

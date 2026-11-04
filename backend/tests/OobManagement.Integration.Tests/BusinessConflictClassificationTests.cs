using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using OobManagement.Application.Models;
using OobManagement.Application.Service;
using Xunit;

namespace OobManagement.Integration.Tests;

/// <summary>模拟发号基础设施故障；不能把所有23505一概包装成正常业务409，电源发送前即失败。</summary>
[Collection("PostgresIntegration")]
public sealed class BusinessConflictClassificationTests(OobApplicationFactory factory) : IClassFixture<OobApplicationFactory>
{
    [Fact]
    public async Task Server_primary_key_collision_remains_system_fault()
    {
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        using var host = HostWithFixedId(id);
        var service = host.Services.GetRequiredService<IServerManagementService>();
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => service.SaveAsync(null, new(factory.UserId, 1), factory.ServerRequest(), CancellationToken.None));
        Assert.Equal("PK_OOB_SERVER", Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
    }

    [Fact]
    public async Task Power_primary_key_collision_does_not_send_device_command_or_pretend_business_conflict()
    {
        factory.Device.Reset();
        var server = await factory.CreateServerAsync();
        var id = long.Parse(server.Id, CultureInfo.InvariantCulture);
        await using (var db = await factory.DatabaseAsync())
        {
            db.PowerOperations.Add(new() { Id = id, ServerId = id, UserId = factory.UserId, RequestId = Guid.NewGuid().ToString(), Action = "On", Status = "Confirmed", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var host = HostWithFixedId(id);
        var service = host.Services.GetRequiredService<IServerManagementService>();
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => service.ExecutePowerAsync(id, new(factory.UserId, 1), new(PowerAction.On, server.Name, Guid.NewGuid().ToString()), CancellationToken.None));
        Assert.Equal("PK_OOB_POWER_OPERATION", Assert.IsType<PostgresException>(exception.InnerException).ConstraintName);
        Assert.Equal(0, factory.Device.WriteCount);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> HostWithFixedId(long id) => factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.RemoveAll<IIdService>();
        services.AddSingleton<IIdService>(new FixedIdService(id));
        services.RemoveAll<IDataProtectionProvider>();
        services.AddSingleton(factory.Services.GetRequiredService<IDataProtectionProvider>());
    }));

    private sealed class FixedIdService(long id) : IIdService { public long NextId() => id; }
}

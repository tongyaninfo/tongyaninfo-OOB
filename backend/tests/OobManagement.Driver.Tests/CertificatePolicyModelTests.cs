using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using Xunit;

namespace OobManagement.Driver.Tests;

/// <summary>仅检查 EF 元数据，生成或读取模型不会打开数据库连接。</summary>
public sealed class CertificatePolicyModelTests
{
    [Fact]
    public void Explicit_false_is_not_a_generated_default_and_database_default_is_true()
    {
        using var db = new OobDbContext(new DbContextOptionsBuilder<OobDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused;Username=placeholder;Password=placeholder").Options);
        var property = db.Model.FindEntityType(typeof(ManagedServer))!.FindProperty(nameof(ManagedServer.IgnoreCertificateErrors))!;
        Assert.True((bool)property.GetDefaultValue()!);
        Assert.Equal(ValueGenerated.Never, property.ValueGenerated);
        Assert.True(new ManagedServer().IgnoreCertificateErrors);
        Assert.False(new ManagedServer { IgnoreCertificateErrors = false }.IgnoreCertificateErrors);
    }
}

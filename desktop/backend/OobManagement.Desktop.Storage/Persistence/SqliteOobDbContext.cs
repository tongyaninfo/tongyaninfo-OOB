using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OobManagement.Desktop.Storage.Common;
using OobManagement.Desktop.Storage.Service.impl;
using OobManagement.Domain.Models;
using OobManagement.Infrastructure.Persistence;
using OobManagement.Infrastructure.Persistence.Service;

namespace OobManagement.Desktop.Storage.Persistence;

/// <summary>共享全部实体及业务约束，仅在桌面进程应用 SQLite 类型和账号表达式。</summary>
public sealed class SqliteOobDbContext(DbContextOptions<SqliteOobDbContext> options) : OobDbContext(options)
{
    public override IOobStorageDialect Storage => SqliteStorageDialect.Instance;

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<OobUser>(builder =>
        {
            builder.ToTable("OOB_USER", table => table.HasCheckConstraint("CK_OOB_USER_Username", "length(trim(\"Username\")) > 0"));
            // SQLite 内置 upper 只覆盖 ASCII；确定性函数与登录查询复用 .NET Unicode 大写规则。
            builder.Property(user => user.NormalizedUsername)
                .HasComputedColumnSql("oob_normalize(\"Username\")", stored: true);
        });
        var converter = new ValueConverter<DateTimeOffset, long>(value => SqliteUtcTime.ToMicroseconds(value), value => SqliteUtcTime.FromMicroseconds(value));
        foreach (var entity in model.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.GetColumnType() == "jsonb") property.SetColumnType("TEXT");
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(converter);
                property.SetColumnType("INTEGER");
            }
        }
    }
}

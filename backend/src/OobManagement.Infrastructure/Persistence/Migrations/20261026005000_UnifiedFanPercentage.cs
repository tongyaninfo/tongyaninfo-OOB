using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OobManagement.Infrastructure.Persistence.Migrations;

/// <summary>显式归并风扇百分比数据，不改变数据库表列；执行前必须保留备份并停止旧采集器。</summary>
[DbContext(typeof(OobDbContext))]
[Migration("20261026005000_UnifiedFanPercentage")]
public sealed class UnifiedFanPercentage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(UnifiedFanPercentageSql.Apply);

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "风扇百分比合并不能从单值数据反推出两份原始记录；请使用迁移前数据库备份及对应 API/PC 镜像回退。");
}

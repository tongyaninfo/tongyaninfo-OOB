using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OobManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOobManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OOB_SERVER",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, comment: "名称"),
                    Brand = table.Column<string>(type: "text", nullable: false, comment: "品牌"),
                    ControllerType = table.Column<string>(type: "text", nullable: false, comment: "带外控制器类型"),
                    Address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false, comment: "管理地址"),
                    Username = table.Column<string>(type: "text", nullable: false, comment: "账号"),
                    PasswordCiphertext = table.Column<string>(type: "text", nullable: false, comment: "设备密码密文"),
                    CertificateFingerprint = table.Column<string>(type: "text", nullable: true, comment: "可信证书指纹"),
                    SamplingIntervalSeconds = table.Column<int>(type: "integer", nullable: false, comment: "采样间隔秒数"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, comment: "是否启用"),
                    IdentityJson = table.Column<string>(type: "jsonb", nullable: false, comment: "设备身份"),
                    CapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false, comment: "设备能力"),
                    LatestTelemetryJson = table.Column<string>(type: "jsonb", nullable: false, comment: "最近实时采样"),
                    ConnectionStatus = table.Column<string>(type: "text", nullable: false, comment: "连接状态"),
                    LastCollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "最后成功采样时间"),
                    LastInventoryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "最后库存刷新时间"),
                    NextPollAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "下次采集时间"),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false, comment: "连续失败次数"),
                    LastError = table.Column<string>(type: "text", nullable: true, comment: "最后安全错误摘要"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "创建时间UTC"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "更新时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_SERVER", x => x.Id);
                    table.CheckConstraint("CK_SERVER_INTERVAL", "\"SamplingIntervalSeconds\" BETWEEN 1 AND 86400");
                },
                comment: "受管理服务器");

            migrationBuilder.CreateTable(
                name: "OOB_USER",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, comment: "账号"),
                    PasswordHash = table.Column<string>(type: "text", nullable: false, comment: "密码哈希"),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, comment: "是否启用"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "创建时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_USER", x => x.Id);
                },
                comment: "系统用户");

            migrationBuilder.CreateTable(
                name: "OOB_METRIC_SERIES",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false, comment: "服务器主键"),
                    SensorKey = table.Column<string>(type: "text", nullable: false, comment: "稳定资源标识"),
                    Name = table.Column<string>(type: "text", nullable: false, comment: "名称"),
                    MetricCode = table.Column<string>(type: "text", nullable: false, comment: "指标编码"),
                    Unit = table.Column<string>(type: "text", nullable: false, comment: "单位")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_METRIC_SERIES", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_METRIC_SERIES_OOB_SERVER_ServerId",
                        column: x => x.ServerId,
                        principalTable: "OOB_SERVER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "传感器指标系列");

            migrationBuilder.CreateTable(
                name: "OOB_POLL_RUN",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false, comment: "服务器主键"),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "开始时间UTC"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "完成时间UTC"),
                    Status = table.Column<string>(type: "text", nullable: false, comment: "执行状态"),
                    MetricCount = table.Column<int>(type: "integer", nullable: false, comment: "读数数量"),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true, comment: "安全错误摘要")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_POLL_RUN", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_POLL_RUN_OOB_SERVER_ServerId",
                        column: x => x.ServerId,
                        principalTable: "OOB_SERVER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "采集执行记录");

            migrationBuilder.CreateTable(
                name: "OOB_SERVER_INVENTORY",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false, comment: "服务器主键"),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "库存采集时间UTC"),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false, comment: "硬件快照")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_SERVER_INVENTORY", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_SERVER_INVENTORY_OOB_SERVER_ServerId",
                        column: x => x.ServerId,
                        principalTable: "OOB_SERVER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "服务器硬件快照");

            migrationBuilder.CreateTable(
                name: "OOB_POWER_OPERATION",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<long>(type: "bigint", nullable: false, comment: "服务器主键"),
                    UserId = table.Column<long>(type: "bigint", nullable: false, comment: "所属用户"),
                    RequestId = table.Column<string>(type: "text", nullable: false, comment: "幂等请求标识"),
                    Action = table.Column<string>(type: "text", nullable: false, comment: "主机电源动作"),
                    Status = table.Column<string>(type: "text", nullable: false, comment: "执行状态"),
                    Message = table.Column<string>(type: "text", nullable: true, comment: "安全结果摘要"),
                    TaskLocation = table.Column<string>(type: "text", nullable: true, comment: "设备任务资源位置"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "创建时间UTC"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "完成时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_POWER_OPERATION", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_POWER_OPERATION_OOB_SERVER_ServerId",
                        column: x => x.ServerId,
                        principalTable: "OOB_SERVER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OOB_POWER_OPERATION_OOB_USER_UserId",
                        column: x => x.UserId,
                        principalTable: "OOB_USER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "主机电源操作意图与审计");

            migrationBuilder.CreateTable(
                name: "OOB_USER_PREFERENCE",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false, comment: "所属用户"),
                    SettingsJson = table.Column<string>(type: "jsonb", nullable: false, comment: "设置白名单内容"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "更新时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_USER_PREFERENCE", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_USER_PREFERENCE_OOB_USER_UserId",
                        column: x => x.UserId,
                        principalTable: "OOB_USER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "账号外观与表格设置");

            migrationBuilder.CreateTable(
                name: "OOB_METRIC_SAMPLE",
                columns: table => new
                {
                    SeriesId = table.Column<long>(type: "bigint", nullable: false, comment: "指标系列主键"),
                    SampledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "采样时间UTC"),
                    Value = table.Column<double>(type: "double precision", nullable: true, comment: "采样值空值表示缺测"),
                    Availability = table.Column<string>(type: "text", nullable: false, comment: "可用性"),
                    Source = table.Column<string>(type: "text", nullable: false, comment: "读数来源"),
                    DeviceTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "设备观察时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_METRIC_SAMPLE", x => new { x.SeriesId, x.SampledAt });
                    table.ForeignKey(
                        name: "FK_OOB_METRIC_SAMPLE_OOB_METRIC_SERIES_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "OOB_METRIC_SERIES",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "时序采样数据");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_METRIC_SAMPLE_SeriesId_SampledAt",
                table: "OOB_METRIC_SAMPLE",
                columns: new[] { "SeriesId", "SampledAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_METRIC_SERIES_ServerId_SensorKey_MetricCode",
                table: "OOB_METRIC_SERIES",
                columns: new[] { "ServerId", "SensorKey", "MetricCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_POLL_RUN_ServerId_StartedAt",
                table: "OOB_POLL_RUN",
                columns: new[] { "ServerId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_POWER_OPERATION_RequestId",
                table: "OOB_POWER_OPERATION",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_POWER_OPERATION_ServerId",
                table: "OOB_POWER_OPERATION",
                column: "ServerId",
                unique: true,
                filter: "\"Status\" IN ('Submitting', 'Submitted', 'Unknown')");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_POWER_OPERATION_ServerId_CreatedAt",
                table: "OOB_POWER_OPERATION",
                columns: new[] { "ServerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_POWER_OPERATION_UserId",
                table: "OOB_POWER_OPERATION",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_SERVER_Address_ControllerType",
                table: "OOB_SERVER",
                columns: new[] { "Address", "ControllerType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_SERVER_INVENTORY_ServerId_CollectedAt",
                table: "OOB_SERVER_INVENTORY",
                columns: new[] { "ServerId", "CollectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_USER_Username",
                table: "OOB_USER",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_USER_PREFERENCE_UserId",
                table: "OOB_USER_PREFERENCE",
                column: "UserId",
                unique: true);

            // 扩展由获准的数据库初始化入口启用；超表与业务表在同一迁移链中创建。
            migrationBuilder.Sql("""SELECT create_hypertable('"OOB_METRIC_SAMPLE"', 'SampledAt', if_not_exists => TRUE);""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OOB_METRIC_SAMPLE");

            migrationBuilder.DropTable(
                name: "OOB_POLL_RUN");

            migrationBuilder.DropTable(
                name: "OOB_POWER_OPERATION");

            migrationBuilder.DropTable(
                name: "OOB_SERVER_INVENTORY");

            migrationBuilder.DropTable(
                name: "OOB_USER_PREFERENCE");

            migrationBuilder.DropTable(
                name: "OOB_METRIC_SERIES");

            migrationBuilder.DropTable(
                name: "OOB_USER");

            migrationBuilder.DropTable(
                name: "OOB_SERVER");
        }
    }
}

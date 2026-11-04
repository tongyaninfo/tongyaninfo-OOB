using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Desktop.Storage.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialDesktopSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OOB_SERVER",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "名称"),
                    Brand = table.Column<string>(type: "TEXT", nullable: false, comment: "品牌"),
                    ControllerType = table.Column<string>(type: "TEXT", nullable: false, comment: "带外控制器类型"),
                    Address = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false, comment: "管理地址"),
                    Username = table.Column<string>(type: "TEXT", nullable: false, comment: "账号"),
                    PasswordCiphertext = table.Column<string>(type: "TEXT", nullable: false, comment: "设备密码密文"),
                    CertificateFingerprint = table.Column<string>(type: "TEXT", nullable: true, comment: "可信证书指纹"),
                    IgnoreCertificateErrors = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true, comment: "是否忽略当前设备HTTPS证书错误；显式指纹仍优先校验"),
                    SamplingIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false, comment: "采样间隔秒数"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否启用"),
                    IdentityJson = table.Column<string>(type: "TEXT", nullable: false, comment: "设备身份"),
                    CapabilitiesJson = table.Column<string>(type: "TEXT", nullable: false, comment: "设备能力"),
                    LatestTelemetryJson = table.Column<string>(type: "TEXT", nullable: false, comment: "最近实时采样"),
                    ConnectionStatus = table.Column<string>(type: "TEXT", nullable: false, comment: "连接状态"),
                    LastCollectedAt = table.Column<long>(type: "INTEGER", nullable: true, comment: "最后成功采样时间"),
                    LastInventoryAt = table.Column<long>(type: "INTEGER", nullable: true, comment: "最后库存刷新时间"),
                    NextPollAt = table.Column<long>(type: "INTEGER", nullable: true, comment: "下次采集时间"),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false, comment: "连续失败次数"),
                    LastError = table.Column<string>(type: "TEXT", nullable: true, comment: "最后安全错误摘要"),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间UTC"),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_SERVER", x => x.Id);
                    table.CheckConstraint("CK_SERVER_INTERVAL", "\"SamplingIntervalSeconds\" BETWEEN 1 AND 86400");
                },
                comment: "受管理服务器");

            migrationBuilder.CreateTable(
                name: "OOB_SYSTEM_EXCEPTION",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "故障发生时间UTC"),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "故障类别"),
                    Level = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "故障级别"),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "故障来源模块"),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "故障发生的受控操作名"),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "稳定故障编码"),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false, comment: "安全结果摘要"),
                    ExceptionType = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true, comment: "异常类型不含消息"),
                    StackTrace = table.Column<string>(type: "text", nullable: true, comment: "脱敏且限长的代码位置"),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true, comment: "关联追踪标识"),
                    ServerId = table.Column<long>(type: "INTEGER", nullable: true, comment: "服务器主键"),
                    UserId = table.Column<long>(type: "INTEGER", nullable: true, comment: "所属用户"),
                    RequestPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true, comment: "请求路径不含查询串"),
                    RequestMethod = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true, comment: "HTTP请求方法"),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true, comment: "HTTP响应状态码")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_SYSTEM_EXCEPTION", x => x.Id);
                },
                comment: "系统、设备与客户端实际故障的脱敏记录");

            migrationBuilder.CreateTable(
                name: "OOB_USER",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "账号"),
                    NormalizedUsername = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, computedColumnSql: "oob_normalize(\"Username\")", stored: true, comment: "数据库计算的去首尾空格大写账号，非删除账号唯一"),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "用户显示姓名"),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false, comment: "密码哈希"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否启用"),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true, comment: "逻辑删除时间UTC，空值表示未删除"),
                    IsAdministrator = table.Column<bool>(type: "INTEGER", nullable: false, comment: "兼容既有系统管理员能力，普通用户管理不可赋予"),
                    CanManageUsers = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否具有用户管理权限"),
                    CanUseConsole = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否具有虚拟控制台及介质操作权限"),
                    CanManageServers = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false, comment: "是否具有服务器配置、连接测试、UID及手动采集维护权限"),
                    CanOperatePower = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false, comment: "是否具有主机电源操作及电源审计核对权限"),
                    AuthVersion = table.Column<long>(type: "INTEGER", nullable: false, comment: "身份安全版本，账号密码状态权限变化后使旧会话失效"),
                    Version = table.Column<long>(type: "INTEGER", nullable: false, comment: "编辑并发版本"),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间UTC"),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_USER", x => x.Id);
                    table.CheckConstraint("CK_OOB_USER_Username", "length(trim(\"Username\")) > 0");
                    table.CheckConstraint("CK_OOB_USER_Versions", "\"Version\" > 0 AND \"AuthVersion\" > 0");
                },
                comment: "系统用户");

            migrationBuilder.CreateTable(
                name: "OOB_METRIC_SERIES",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    ServerId = table.Column<long>(type: "INTEGER", nullable: false, comment: "服务器主键"),
                    SensorKey = table.Column<string>(type: "TEXT", nullable: false, comment: "稳定资源标识"),
                    Name = table.Column<string>(type: "TEXT", nullable: false, comment: "名称"),
                    MetricCode = table.Column<string>(type: "TEXT", nullable: false, comment: "指标编码"),
                    Unit = table.Column<string>(type: "TEXT", nullable: false, comment: "单位")
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    ServerId = table.Column<long>(type: "INTEGER", nullable: false, comment: "服务器主键"),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "开始时间UTC"),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "完成时间UTC"),
                    Status = table.Column<string>(type: "TEXT", nullable: false, comment: "执行状态"),
                    MetricCount = table.Column<int>(type: "INTEGER", nullable: false, comment: "读数数量"),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true, comment: "安全错误摘要")
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    ServerId = table.Column<long>(type: "INTEGER", nullable: false, comment: "服务器主键"),
                    CollectedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "库存采集时间UTC"),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false, comment: "硬件快照")
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    ServerId = table.Column<long>(type: "INTEGER", nullable: false, comment: "服务器主键"),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false, comment: "所属用户"),
                    RequestId = table.Column<string>(type: "TEXT", nullable: false, comment: "幂等请求标识"),
                    Action = table.Column<string>(type: "TEXT", nullable: false, comment: "主机电源动作"),
                    Status = table.Column<string>(type: "TEXT", nullable: false, comment: "执行状态"),
                    Message = table.Column<string>(type: "TEXT", nullable: true, comment: "安全结果摘要"),
                    TaskLocation = table.Column<string>(type: "TEXT", nullable: true, comment: "设备任务资源位置"),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间UTC"),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true, comment: "完成时间UTC")
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
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false, comment: "所属用户"),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false, comment: "设置白名单内容"),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间UTC")
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
                    SeriesId = table.Column<long>(type: "INTEGER", nullable: false, comment: "指标系列主键"),
                    SampledAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "采样时间UTC"),
                    Value = table.Column<double>(type: "REAL", nullable: true, comment: "采样值空值表示缺测"),
                    Availability = table.Column<string>(type: "TEXT", nullable: false, comment: "可用性"),
                    Source = table.Column<string>(type: "TEXT", nullable: false, comment: "读数来源"),
                    DeviceTime = table.Column<long>(type: "INTEGER", nullable: true, comment: "设备观察时间UTC")
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
                name: "IX_OOB_SYSTEM_EXCEPTION_Kind_OccurredAt",
                table: "OOB_SYSTEM_EXCEPTION",
                columns: new[] { "Kind", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_SYSTEM_EXCEPTION_OccurredAt_Id",
                table: "OOB_SYSTEM_EXCEPTION",
                columns: new[] { "OccurredAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_SYSTEM_EXCEPTION_ServerId_OccurredAt",
                table: "OOB_SYSTEM_EXCEPTION",
                columns: new[] { "ServerId", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_USER_NormalizedUsername",
                table: "OOB_USER",
                column: "NormalizedUsername",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_USER_PREFERENCE_UserId",
                table: "OOB_USER_PREFERENCE",
                column: "UserId",
                unique: true);
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
                name: "OOB_SYSTEM_EXCEPTION");

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

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Desktop.Storage.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OOB_MCP_CALL_LOG",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    CallId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "稳定平台调用标识"),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "发生时间UTC"),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true, comment: "完成时间UTC"),
                    ClientId = table.Column<long>(type: "INTEGER", nullable: true, comment: "调用方业务主键"),
                    ClientName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true, comment: "调用方名称快照"),
                    UserId = table.Column<long>(type: "INTEGER", nullable: true, comment: "绑定用户主键"),
                    Method = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "协议方法"),
                    Tool = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true, comment: "工具名称"),
                    ServerId = table.Column<long>(type: "INTEGER", nullable: true, comment: "设备主键"),
                    SourceIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, comment: "可信来源地址"),
                    ProtocolVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true, comment: "MCP协议版本"),
                    Result = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "实际执行结果"),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true, comment: "稳定错误码"),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true, comment: "实际HTTP状态"),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true, comment: "耗时毫秒"),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, comment: "追踪标识"),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true, comment: "协议请求标识安全摘要"),
                    ParametersSummary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true, comment: "参数字段白名单摘要"),
                    OutputSummary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true, comment: "输出安全摘要"),
                    PolicySummary = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true, comment: "命中策略摘要"),
                    ExceptionId = table.Column<long>(type: "INTEGER", nullable: true, comment: "关联故障记录")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MCP_CALL_LOG", x => x.Id);
                },
                comment: "MCP 独立调用审计");

            migrationBuilder.CreateTable(
                name: "OOB_MCP_CLIENT",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "调用方名称"),
                    OAuthClientId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "OAuth 客户端公开标识"),
                    ClientType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, comment: "公共或保密客户端"),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false, comment: "绑定用户主键"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否启用"),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true, comment: "到期时间UTC"),
                    ServerScope = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, comment: "全部或指定设备范围"),
                    Version = table.Column<long>(type: "INTEGER", nullable: false, comment: "策略并发版本"),
                    OriginsJson = table.Column<string>(type: "TEXT", nullable: false, comment: "浏览器Origin白名单"),
                    RedirectUrisJson = table.Column<string>(type: "TEXT", nullable: false, comment: "精确OAuth回调地址"),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "创建时间UTC"),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间UTC"),
                    LastCalledAt = table.Column<long>(type: "INTEGER", nullable: true, comment: "最近调用时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MCP_CLIENT", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_MCP_CLIENT_OOB_USER_UserId",
                        column: x => x.UserId,
                        principalTable: "OOB_USER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "MCP 调用方及策略");

            migrationBuilder.CreateTable(
                name: "OOB_MCP_CONFIG_AUDIT",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "发生时间UTC"),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false, comment: "绑定用户主键"),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "配置操作"),
                    ObjectId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "目标标识"),
                    PreviousVersion = table.Column<long>(type: "INTEGER", nullable: false, comment: "操作前版本"),
                    CurrentVersion = table.Column<long>(type: "INTEGER", nullable: false, comment: "操作后版本"),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false, comment: "脱敏配置变更说明")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MCP_CONFIG_AUDIT", x => x.Id);
                },
                comment: "MCP 配置与授权变更审计");

            migrationBuilder.CreateTable(
                name: "OOB_MCP_SETTING",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    Slot = table.Column<int>(type: "INTEGER", nullable: false, comment: "单例配置槽位"),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, comment: "是否启用"),
                    Version = table.Column<long>(type: "INTEGER", nullable: false, comment: "策略并发版本"),
                    RequestsPerMinute = table.Column<int>(type: "INTEGER", nullable: false, comment: "每调用方每分钟请求限额"),
                    ClientConcurrency = table.Column<int>(type: "INTEGER", nullable: false, comment: "每调用方并发上限"),
                    GlobalConcurrency = table.Column<int>(type: "INTEGER", nullable: false, comment: "全局并发上限"),
                    QueryTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false, comment: "查询超时秒数"),
                    MaxRequestBytes = table.Column<int>(type: "INTEGER", nullable: false, comment: "请求字节上限"),
                    MaxResponseBytes = table.Column<int>(type: "INTEGER", nullable: false, comment: "响应字节上限"),
                    EnabledToolsJson = table.Column<string>(type: "TEXT", nullable: false, comment: "全局启用工具集合"),
                    UpdatedByUserId = table.Column<long>(type: "INTEGER", nullable: false, comment: "最后配置操作人"),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false, comment: "更新时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MCP_SETTING", x => x.Id);
                    table.CheckConstraint("CK_OOB_MCP_SETTING_Slot", "\"Slot\" = 1");
                },
                comment: "MCP 单例配置");

            migrationBuilder.CreateTable(
                name: "OOB_MCP_CLIENT_NETWORK",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    ClientId = table.Column<long>(type: "INTEGER", nullable: false, comment: "调用方业务主键"),
                    Network = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "规范化IP或CIDR"),
                    AddressFamily = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false, comment: "地址族")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MCP_CLIENT_NETWORK", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_MCP_CLIENT_NETWORK_OOB_MCP_CLIENT_ClientId",
                        column: x => x.ClientId,
                        principalTable: "OOB_MCP_CLIENT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "MCP 来源网络白名单");

            migrationBuilder.CreateTable(
                name: "OOB_MCP_CLIENT_SERVER",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    ClientId = table.Column<long>(type: "INTEGER", nullable: false, comment: "调用方业务主键"),
                    ServerId = table.Column<long>(type: "INTEGER", nullable: false, comment: "设备主键")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MCP_CLIENT_SERVER", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_MCP_CLIENT_SERVER_OOB_MCP_CLIENT_ClientId",
                        column: x => x.ClientId,
                        principalTable: "OOB_MCP_CLIENT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OOB_MCP_CLIENT_SERVER_OOB_SERVER_ServerId",
                        column: x => x.ServerId,
                        principalTable: "OOB_SERVER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "MCP 调用方设备白名单");

            migrationBuilder.CreateTable(
                name: "OOB_MCP_CLIENT_TOOL",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false, comment: "统一发号主键"),
                    ClientId = table.Column<long>(type: "INTEGER", nullable: false, comment: "调用方业务主键"),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, comment: "获准工具名")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MCP_CLIENT_TOOL", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OOB_MCP_CLIENT_TOOL_OOB_MCP_CLIENT_ClientId",
                        column: x => x.ClientId,
                        principalTable: "OOB_MCP_CLIENT",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "MCP 调用方工具白名单");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CALL_LOG_CallId",
                table: "OOB_MCP_CALL_LOG",
                column: "CallId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CALL_LOG_ClientId_OccurredAt",
                table: "OOB_MCP_CALL_LOG",
                columns: new[] { "ClientId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CALL_LOG_OccurredAt_Id",
                table: "OOB_MCP_CALL_LOG",
                columns: new[] { "OccurredAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CALL_LOG_Result_OccurredAt",
                table: "OOB_MCP_CALL_LOG",
                columns: new[] { "Result", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CALL_LOG_ServerId_OccurredAt",
                table: "OOB_MCP_CALL_LOG",
                columns: new[] { "ServerId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CALL_LOG_Tool_OccurredAt",
                table: "OOB_MCP_CALL_LOG",
                columns: new[] { "Tool", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CALL_LOG_TraceId",
                table: "OOB_MCP_CALL_LOG",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CLIENT_OAuthClientId",
                table: "OOB_MCP_CLIENT",
                column: "OAuthClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CLIENT_UserId",
                table: "OOB_MCP_CLIENT",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CLIENT_NETWORK_ClientId_Network",
                table: "OOB_MCP_CLIENT_NETWORK",
                columns: new[] { "ClientId", "Network" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CLIENT_SERVER_ClientId_ServerId",
                table: "OOB_MCP_CLIENT_SERVER",
                columns: new[] { "ClientId", "ServerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CLIENT_SERVER_ServerId",
                table: "OOB_MCP_CLIENT_SERVER",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CLIENT_TOOL_ClientId_ToolName",
                table: "OOB_MCP_CLIENT_TOOL",
                columns: new[] { "ClientId", "ToolName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_CONFIG_AUDIT_OccurredAt_Id",
                table: "OOB_MCP_CONFIG_AUDIT",
                columns: new[] { "OccurredAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MCP_SETTING_Slot",
                table: "OOB_MCP_SETTING",
                column: "Slot",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OOB_MCP_CALL_LOG");

            migrationBuilder.DropTable(
                name: "OOB_MCP_CLIENT_NETWORK");

            migrationBuilder.DropTable(
                name: "OOB_MCP_CLIENT_SERVER");

            migrationBuilder.DropTable(
                name: "OOB_MCP_CLIENT_TOOL");

            migrationBuilder.DropTable(
                name: "OOB_MCP_CONFIG_AUDIT");

            migrationBuilder.DropTable(
                name: "OOB_MCP_SETTING");

            migrationBuilder.DropTable(
                name: "OOB_MCP_CLIENT");
        }
    }
}

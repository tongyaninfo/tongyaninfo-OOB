using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemExceptionLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OOB_SYSTEM_EXCEPTION",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键"),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "故障发生时间UTC"),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, comment: "故障类别"),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "故障级别"),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, comment: "故障来源模块"),
                    Operation = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, comment: "故障发生的受控操作名"),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, comment: "稳定故障编码"),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, comment: "安全结果摘要"),
                    ExceptionType = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, comment: "异常类型不含消息"),
                    StackTrace = table.Column<string>(type: "text", nullable: true, comment: "脱敏且限长的代码位置"),
                    TraceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true, comment: "关联追踪标识"),
                    ServerId = table.Column<long>(type: "bigint", nullable: true, comment: "服务器主键"),
                    UserId = table.Column<long>(type: "bigint", nullable: true, comment: "所属用户"),
                    RequestPath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true, comment: "请求路径不含查询串"),
                    RequestMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true, comment: "HTTP请求方法"),
                    HttpStatusCode = table.Column<int>(type: "integer", nullable: true, comment: "HTTP响应状态码")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_SYSTEM_EXCEPTION", x => x.Id);
                },
                comment: "系统、设备与客户端实际故障的脱敏记录");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OOB_SYSTEM_EXCEPTION");
        }
    }
}

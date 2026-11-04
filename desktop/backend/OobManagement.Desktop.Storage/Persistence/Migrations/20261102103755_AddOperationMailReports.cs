using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Desktop.Storage.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationMailReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HtmlBody",
                table: "OOB_MAIL_MESSAGE",
                type: "text",
                nullable: true,
                comment: "可信内部报表的HTML快照，普通邮件为空，仅详情鉴权后读取");

            migrationBuilder.AddColumn<string>(
                name: "ReportSchedulesJson",
                table: "OOB_MAIL_CONFIGURATION",
                type: "text",
                nullable: false,
                defaultValue: "[]",
                comment: "日周月运行报表计划JSON，默认全部关闭，启用起点由服务端维护");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HtmlBody",
                table: "OOB_MAIL_MESSAGE");

            migrationBuilder.DropColumn(
                name: "ReportSchedulesJson",
                table: "OOB_MAIL_CONFIGURATION");
        }
    }
}

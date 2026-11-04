using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerServerCertificatePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IgnoreCertificateErrors",
                table: "OOB_SERVER",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                comment: "是否忽略当前设备HTTPS证书错误；显式指纹仍优先校验");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IgnoreCertificateErrors",
                table: "OOB_SERVER");
        }
    }
}

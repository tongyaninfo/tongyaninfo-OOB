using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServerAndPowerPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CanManageServers",
                table: "OOB_USER",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                comment: "是否具有服务器配置、连接测试、UID及手动采集维护权限");

            migrationBuilder.AddColumn<bool>(
                name: "CanOperatePower",
                table: "OOB_USER",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                comment: "是否具有主机电源操作及电源审计核对权限");

            // 仅迁移时的系统管理员保留原有服务器/电源能力；普通用户及今后新增账号默认无此权限。
            // 安全与编辑版本同时递增，阻止旧Cookie和缺少新增勾选项的旧表单继续操作。
            migrationBuilder.Sql("""
                UPDATE "OOB_USER"
                SET "CanManageServers" = "IsAdministrator", "CanOperatePower" = "IsAdministrator",
                    "AuthVersion" = "AuthVersion" + 1, "Version" = "Version" + 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CanManageServers",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "CanOperatePower",
                table: "OOB_USER");
        }
    }
}

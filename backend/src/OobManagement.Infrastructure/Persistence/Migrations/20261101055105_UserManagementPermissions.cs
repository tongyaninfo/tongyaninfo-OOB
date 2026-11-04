using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserManagementPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 在任何结构修改前拒绝规范化冲突；禁止迁移静默合并、改名或删除已有账号。
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "OOB_USER" WHERE length(btrim("Username")) = 0)
                       OR EXISTS (SELECT 1 FROM "OOB_USER" GROUP BY upper(btrim("Username")) HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'OOB_USER_NORMALIZATION_CONFLICT: 现有账号去空格或忽略大小写后重复，迁移已停止';
                    END IF;
                END $migration$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_OOB_USER_Username",
                table: "OOB_USER");

            migrationBuilder.AddColumn<long>(
                name: "AuthVersion",
                table: "OOB_USER",
                type: "bigint",
                nullable: false,
                defaultValue: 1L,
                comment: "身份安全版本，账号密码状态权限变化后使旧会话失效");

            migrationBuilder.AddColumn<bool>(
                name: "CanManageUsers",
                table: "OOB_USER",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                comment: "是否具有用户管理权限");

            migrationBuilder.AddColumn<bool>(
                name: "CanUseConsole",
                table: "OOB_USER",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                comment: "是否具有虚拟控制台及介质操作权限");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "OOB_USER",
                type: "timestamp with time zone",
                nullable: true,
                comment: "逻辑删除时间UTC，空值表示未删除");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "OOB_USER",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                comment: "用户显示姓名");

            migrationBuilder.AddColumn<bool>(
                name: "IsAdministrator",
                table: "OOB_USER",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                comment: "兼容既有系统管理员能力，普通用户管理不可赋予");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "OOB_USER",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                comment: "更新时间UTC");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "OOB_USER",
                type: "bigint",
                nullable: false,
                defaultValue: 1L,
                comment: "编辑并发版本");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedUsername",
                table: "OOB_USER",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                computedColumnSql: "upper(btrim(\"Username\"))",
                stored: true,
                comment: "数据库计算的去首尾空格大写账号，非删除账号唯一");

            // 旧系统所有已存在账号原本均具有admin能力；只回填迁移时既有行，新增账号仍默认无权限。
            migrationBuilder.Sql("""
                UPDATE "OOB_USER"
                SET "Username" = btrim("Username"), "DisplayName" = btrim("Username"),
                    "IsAdministrator" = TRUE, "CanManageUsers" = TRUE, "CanUseConsole" = TRUE,
                    "AuthVersion" = 1, "Version" = 1, "UpdatedAt" = "CreatedAt";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_USER_NormalizedUsername",
                table: "OOB_USER",
                column: "NormalizedUsername",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OOB_USER_Username",
                table: "OOB_USER",
                sql: "length(btrim(\"Username\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OOB_USER_Versions",
                table: "OOB_USER",
                sql: "\"Version\" > 0 AND \"AuthVersion\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OOB_USER_NormalizedUsername",
                table: "OOB_USER");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OOB_USER_Username",
                table: "OOB_USER");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OOB_USER_Versions",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "NormalizedUsername",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "AuthVersion",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "CanManageUsers",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "CanUseConsole",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "IsAdministrator",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "OOB_USER");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "OOB_USER");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_USER_Username",
                table: "OOB_USER",
                column: "Username",
                unique: true);
        }
    }
}

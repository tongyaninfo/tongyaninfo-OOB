using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Infrastructure.Persistence.OAuthMigrations
{
    /// <inheritdoc />
    public partial class AddMcpOAuthStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false, comment: "OAuth 组件字段：Id"),
                    ApplicationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：ApplicationType"),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, comment: "OAuth 组件字段：ClientId"),
                    ClientSecret = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：ClientSecret"),
                    ClientType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：ClientType"),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：ConcurrencyToken"),
                    ConsentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：ConsentType"),
                    DisplayName = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：DisplayName"),
                    DisplayNames = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：DisplayNames"),
                    JsonWebKeySet = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：JsonWebKeySet"),
                    Permissions = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Permissions"),
                    PostLogoutRedirectUris = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：PostLogoutRedirectUris"),
                    Properties = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Properties"),
                    RedirectUris = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：RedirectUris"),
                    Requirements = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Requirements"),
                    Settings = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Settings")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                },
                comment: "MCP OAuth 组件存储：OpenIddictApplications");

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false, comment: "OAuth 组件字段：Id"),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：ConcurrencyToken"),
                    Description = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Description"),
                    Descriptions = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Descriptions"),
                    DisplayName = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：DisplayName"),
                    DisplayNames = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：DisplayNames"),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true, comment: "OAuth 组件字段：Name"),
                    Properties = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Properties"),
                    Resources = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Resources")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                },
                comment: "MCP OAuth 组件存储：OpenIddictScopes");

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false, comment: "OAuth 组件字段：Id"),
                    ApplicationId = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：ApplicationId"),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：ConcurrencyToken"),
                    CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, comment: "OAuth 组件字段：CreationDate"),
                    Properties = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Properties"),
                    Scopes = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Scopes"),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：Status"),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true, comment: "OAuth 组件字段：Subject"),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：Type")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_OpenIddictApplications_Application~",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                },
                comment: "MCP OAuth 组件存储：OpenIddictAuthorizations");

            migrationBuilder.CreateTable(
                name: "OpenIddictTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false, comment: "OAuth 组件字段：Id"),
                    ApplicationId = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：ApplicationId"),
                    AuthorizationId = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：AuthorizationId"),
                    ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：ConcurrencyToken"),
                    CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, comment: "OAuth 组件字段：CreationDate"),
                    ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, comment: "OAuth 组件字段：ExpirationDate"),
                    Payload = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Payload"),
                    Properties = table.Column<string>(type: "text", nullable: true, comment: "OAuth 组件字段：Properties"),
                    RedemptionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, comment: "OAuth 组件字段：RedemptionDate"),
                    ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, comment: "OAuth 组件字段：ReferenceId"),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true, comment: "OAuth 组件字段：Status"),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true, comment: "OAuth 组件字段：Subject"),
                    Type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true, comment: "OAuth 组件字段：Type")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id");
                },
                comment: "MCP OAuth 组件存储：OpenIddictTokens");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictApplications_ClientId",
                table: "OpenIddictApplications",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
                table: "OpenIddictAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictScopes_Name",
                table: "OpenIddictScopes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
                table: "OpenIddictTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_AuthorizationId",
                table: "OpenIddictTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ReferenceId",
                table: "OpenIddictTokens",
                column: "ReferenceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenIddictScopes");

            migrationBuilder.DropTable(
                name: "OpenIddictTokens");

            migrationBuilder.DropTable(
                name: "OpenIddictAuthorizations");

            migrationBuilder.DropTable(
                name: "OpenIddictApplications");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OobManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMailSending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OOB_MAIL_CONFIGURATION",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键"),
                    Slot = table.Column<int>(type: "integer", nullable: false, comment: "固定为1的唯一配置槽，禁止创建多个SMTP配置"),
                    Host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false, comment: "SMTP主机名或地址，不含协议和路径"),
                    Port = table.Column<int>(type: "integer", nullable: false, comment: "SMTP端口，默认465"),
                    SecurityMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, comment: "SslOnConnect或强制StartTls，严格验证证书"),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, comment: "发件邮箱地址"),
                    Username = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, comment: "SMTP认证账号，空值使用发件邮箱"),
                    PasswordCiphertext = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false, comment: "宿主DataProtection邮件专用purpose保护的密码密文"),
                    Version = table.Column<long>(type: "bigint", nullable: false, comment: "配置并发版本，保存及发送前核验"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "最近保存时间UTC")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MAIL_CONFIGURATION", x => x.Id);
                    table.CheckConstraint("CK_OOB_MAIL_CONFIGURATION_Options", "\"Port\" BETWEEN 1 AND 65535 AND \"Version\" > 0 AND \"SecurityMode\" IN ('SslOnConnect','StartTls')");
                    table.CheckConstraint("CK_OOB_MAIL_CONFIGURATION_Slot", "\"Slot\" = 1");
                },
                comment: "唯一SMTP发件配置；密码由宿主密钥以邮件独立用途加密");

            migrationBuilder.CreateTable(
                name: "OOB_MAIL_MESSAGE",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false, comment: "统一发号主键"),
                    RequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, comment: "调用方稳定请求标识，重复请求不得再次发送"),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, comment: "调用人、种类、收件人和内容摘要，阻止同标识不同内容"),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "Test测试、Manual手动、System可信内部发件"),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, comment: "本次发送冻结的发件邮箱"),
                    ToAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, comment: "唯一收件邮箱"),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false, comment: "本次邮件主题"),
                    TextBody = table.Column<string>(type: "text", nullable: false, comment: "本次纯文本正文，仅详情鉴权后读取"),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, comment: "发送中、服务器受理、明确失败或结果未知；重启遗留发送中按未知展示"),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, comment: "固定安全错误编码，不保存服务器自由文本"),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true, comment: "固定安全中文结果，不含凭据或协议原文"),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: true, comment: "明确操作的真实用户，可信系统发信为空"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, comment: "发送意图创建时间UTC"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true, comment: "本地记录结果时间UTC，不代表远端送达时间")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OOB_MAIL_MESSAGE", x => x.Id);
                    table.CheckConstraint("CK_OOB_MAIL_MESSAGE_Kind", "(\"Kind\" IN ('Test','Manual') AND \"CreatedByUserId\" IS NOT NULL) OR (\"Kind\" = 'System' AND \"CreatedByUserId\" IS NULL)");
                    table.CheckConstraint("CK_OOB_MAIL_MESSAGE_Status", "\"Status\" IN ('Sending','Accepted','Failed','Unknown')");
                    table.ForeignKey(
                        name: "FK_OOB_MAIL_MESSAGE_OOB_USER_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "OOB_USER",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                },
                comment: "本系统发件意图与安全结果；SMTP受理不等于已送达");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MAIL_CONFIGURATION_Slot",
                table: "OOB_MAIL_CONFIGURATION",
                column: "Slot",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MAIL_MESSAGE_CreatedAt_Id",
                table: "OOB_MAIL_MESSAGE",
                columns: new[] { "CreatedAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MAIL_MESSAGE_CreatedByUserId",
                table: "OOB_MAIL_MESSAGE",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_OOB_MAIL_MESSAGE_RequestId",
                table: "OOB_MAIL_MESSAGE",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OOB_MAIL_CONFIGURATION");

            migrationBuilder.DropTable(
                name: "OOB_MAIL_MESSAGE");
        }
    }
}

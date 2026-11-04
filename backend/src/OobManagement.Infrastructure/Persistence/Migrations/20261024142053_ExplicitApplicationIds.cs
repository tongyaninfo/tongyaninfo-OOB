using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OobManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitApplicationIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_USER_PREFERENCE",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_USER",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_SERVER_INVENTORY",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_SERVER",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_POWER_OPERATION",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_POLL_RUN",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_METRIC_SERIES",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .OldAnnotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_USER_PREFERENCE",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_USER",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_SERVER_INVENTORY",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_SERVER",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_POWER_OPERATION",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_POLL_RUN",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AlterColumn<long>(
                name: "Id",
                table: "OOB_METRIC_SERIES",
                type: "bigint",
                nullable: false,
                comment: "统一发号主键",
                oldClrType: typeof(long),
                oldType: "bigint",
                oldComment: "统一发号主键")
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);
        }
    }
}

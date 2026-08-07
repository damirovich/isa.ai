using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "must_change_password",
                schema: "core",
                table: "app_user",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                schema: "core",
                table: "app_user",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "security_stamp",
                schema: "core",
                table: "app_user",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "must_change_password",
                schema: "core",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "password_hash",
                schema: "core",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "security_stamp",
                schema: "core",
                table: "app_user");
        }
    }
}

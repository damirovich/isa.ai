using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppearanceRevoke : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "revoke_reason",
                schema: "investigation",
                table: "appearance",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "revoked_at_utc",
                schema: "investigation",
                table: "appearance",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revoked_by_user_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "revoke_reason",
                schema: "investigation",
                table: "appearance");

            migrationBuilder.DropColumn(
                name: "revoked_at_utc",
                schema: "investigation",
                table: "appearance");

            migrationBuilder.DropColumn(
                name: "revoked_by_user_id",
                schema: "investigation",
                table: "appearance");
        }
    }
}

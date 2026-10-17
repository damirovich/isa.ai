using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <summary>
    /// ADR-0054: настройки поиска и верификации (<c>media.setting</c>) и режим разбора на решении верификации. У всех
    /// решений, записанных до миграции, режим — «два лица» (2): так они и принимались; смена режима ничего не пересчитывает.
    /// </summary>
    public partial class MediaSettingsAndConfirmationMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "confirmation_mode",
                schema: "media",
                table: "verification_decision",
                type: "integer",
                nullable: false,
                defaultValue: 2); // ConfirmationMode.TwoPerson — прежние решения приняты в режиме двух лиц

            migrationBuilder.CreateTable(
                name: "setting",
                schema: "media",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_setting", x => x.key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setting",
                schema: "media");

            migrationBuilder.DropColumn(
                name: "confirmation_mode",
                schema: "media",
                table: "verification_decision");
        }
    }
}

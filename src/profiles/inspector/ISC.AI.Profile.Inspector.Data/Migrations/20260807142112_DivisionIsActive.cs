using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class DivisionIsActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                schema: "inspector",
                table: "division",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_active",
                schema: "inspector",
                table: "division");
        }
    }
}

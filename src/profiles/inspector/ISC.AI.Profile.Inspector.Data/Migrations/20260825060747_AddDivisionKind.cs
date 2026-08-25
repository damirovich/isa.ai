using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDivisionKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Существующие подразделения помечаются территориальными (историческое умолчание
            // справочника, DivisionKind.Territorial = 1): нулевого значения в enum нет.
            migrationBuilder.AddColumn<int>(
                name: "kind",
                schema: "inspector",
                table: "division",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                schema: "inspector",
                table: "division");
        }
    }
}

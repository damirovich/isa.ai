using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.DocFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class RegNumberCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reg_counter",
                schema: "docflow",
                columns: table => new
                {
                    direction = table.Column<int>(type: "integer", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    last_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reg_counter", x => new { x.direction, x.year });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reg_counter",
                schema: "docflow");
        }
    }
}

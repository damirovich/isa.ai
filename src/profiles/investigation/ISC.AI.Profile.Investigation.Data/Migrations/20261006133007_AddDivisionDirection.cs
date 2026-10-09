using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// Отметка отдела ОН/ОУ у подразделения (ТЭ-008, ADR-0039): столбец <c>investigation.division.direction</c>
    /// (NULL — своей отметки нет, действует отметка вышестоящего) и ограничение <c>ck_division_direction</c> — только
    /// ОН (1) или ОУ (2). У дела отдельного поля нет: отдел дела — по его подразделению. Существующие подразделения
    /// остаются без отметки, пока администратор не отметит отделы в справочнике.
    /// </summary>
    public partial class AddDivisionDirection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "direction",
                schema: "investigation",
                table: "division",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_division_direction",
                schema: "investigation",
                table: "division",
                sql: "direction IS NULL OR direction IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_division_direction",
                schema: "investigation",
                table: "division");

            migrationBuilder.DropColumn(
                name: "direction",
                schema: "investigation",
                table: "division");
        }
    }
}

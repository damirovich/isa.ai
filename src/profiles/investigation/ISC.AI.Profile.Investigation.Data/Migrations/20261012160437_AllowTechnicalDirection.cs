using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// Отдел ОТМ у подразделения (ТС-014, ADR-0039, решение Заказчика от 12.10.2026): ограничение
    /// <c>ck_division_direction</c> принимает ОН (1), ОУ (2) и ОТМ (3). Данные не меняются.
    /// </summary>
    public partial class AllowTechnicalDirection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_division_direction",
                schema: "investigation",
                table: "division");

            migrationBuilder.AddCheckConstraint(
                name: "ck_division_direction",
                schema: "investigation",
                table: "division",
                sql: "direction IS NULL OR direction IN (1, 2, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Откат: отметка ОТМ снимается (подразделения становятся «без своей отметки»), иначе старое ограничение
            // не встанет на таблицу. Дела не трогаются — отдел у них вычисляется по подразделению.
            migrationBuilder.Sql("UPDATE investigation.division SET direction = NULL WHERE direction = 3;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_division_direction",
                schema: "investigation",
                table: "division");

            migrationBuilder.AddCheckConstraint(
                name: "ck_division_direction",
                schema: "investigation",
                table: "division",
                sql: "direction IS NULL OR direction IN (1, 2)");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonCaseIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_person_case_id",
                schema: "investigation",
                table: "person",
                column: "case_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_person_case_id",
                schema: "investigation",
                table: "person");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "origin",
                schema: "media",
                table: "search_session",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "suggested_person_ref",
                schema: "media",
                table: "search_session",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_session_case_id_suggested_person_ref_probe_face_id",
                schema: "media",
                table: "search_session",
                columns: new[] { "case_id", "suggested_person_ref", "probe_face_id" },
                filter: "suggested_person_ref IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_search_session_case_id_suggested_person_ref_probe_face_id",
                schema: "media",
                table: "search_session");

            migrationBuilder.DropColumn(
                name: "origin",
                schema: "media",
                table: "search_session");

            migrationBuilder.DropColumn(
                name: "suggested_person_ref",
                schema: "media",
                table: "search_session");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchMultiProbe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int[]>(
                name: "probe_face_ids",
                schema: "media",
                table: "search_session",
                type: "integer[]",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "probe_face_id",
                schema: "media",
                table: "search_candidate",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "probe_match_count",
                schema: "media",
                table: "search_candidate",
                type: "smallint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "probe_face_ids",
                schema: "media",
                table: "search_session");

            migrationBuilder.DropColumn(
                name: "probe_face_id",
                schema: "media",
                table: "search_candidate");

            migrationBuilder.DropColumn(
                name: "probe_match_count",
                schema: "media",
                table: "search_candidate");
        }
    }
}

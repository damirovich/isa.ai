using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class SuggestionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suggestion_run",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    trigger = table.Column<int>(type: "integer", nullable: false),
                    references_checked = table.Column<int>(type: "integer", nullable: false),
                    sessions_created = table.Column<int>(type: "integer", nullable: false),
                    candidates_created = table.Column<int>(type: "integer", nullable: false),
                    max_cosine_distance = table.Column<double>(type: "double precision", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suggestion_run", x => x.id);
                    table.ForeignKey(
                        name: "fk_suggestion_run_asset_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_run_asset_id_case_id_created_at",
                schema: "media",
                table: "suggestion_run",
                columns: new[] { "asset_id", "case_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_run_case_id",
                schema: "media",
                table: "suggestion_run",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_suggestion_run_classification_division_id",
                schema: "media",
                table: "suggestion_run",
                columns: new[] { "classification", "division_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "suggestion_run",
                schema: "media");
        }
    }
}

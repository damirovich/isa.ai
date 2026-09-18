using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaseClosureAct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "case_closure_act",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    executed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    executed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    media_assets_total = table.Column<int>(type: "integer", nullable: false),
                    assets_affected = table.Column<int>(type: "integer", nullable: false),
                    templates_removed = table.Column<int>(type: "integer", nullable: false),
                    crops_removed = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_closure_act", x => x.id);
                    table.ForeignKey(
                        name: "fk_case_closure_act_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_case_closure_act_case_id",
                schema: "investigation",
                table: "case_closure_act",
                column: "case_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_closure_act",
                schema: "investigation");
        }
    }
}

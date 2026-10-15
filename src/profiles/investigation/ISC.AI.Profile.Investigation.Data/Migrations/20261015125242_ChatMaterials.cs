using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChatMaterials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "case_id",
                schema: "investigation",
                table: "case_chat",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.CreateTable(
                name: "case_corpus_entry",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    core_document_id = table.Column<int>(type: "integer", nullable: false),
                    source_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    synced_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_corpus_entry", x => x.id);
                    table.ForeignKey(
                        name: "fk_case_corpus_entry_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_case_corpus_entry_case_id",
                schema: "investigation",
                table: "case_corpus_entry",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_corpus_entry_kind_source_id",
                schema: "investigation",
                table: "case_corpus_entry",
                columns: new[] { "kind", "source_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_corpus_entry",
                schema: "investigation");

            migrationBuilder.AlterColumn<int>(
                name: "case_id",
                schema: "investigation",
                table: "case_chat",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}

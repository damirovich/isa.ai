using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class TranscriptCorpus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "transcript_index_link",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    core_document_id = table.Column<int>(type: "integer", nullable: false),
                    indexed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcript_index_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_transcript_index_link_asset_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transcript_index_link_asset_id",
                schema: "media",
                table: "transcript_index_link",
                column: "asset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transcript_index_link_core_document_id",
                schema: "media",
                table: "transcript_index_link",
                column: "core_document_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transcript_index_link",
                schema: "media");
        }
    }
}

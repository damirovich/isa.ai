using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialInspector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inspector");

            migrationBuilder.CreateTable(
                name: "legalNorm",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_legalNorm", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "normDocumentLink",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    legalNormId = table.Column<int>(type: "integer", nullable: false),
                    documentId = table.Column<int>(type: "integer", nullable: false),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_normDocumentLink", x => x.id);
                    table.ForeignKey(
                        name: "fK_normDocumentLink_legalNorm_legalNormId",
                        column: x => x.legalNormId,
                        principalSchema: "inspector",
                        principalTable: "legalNorm",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "normRevision",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    normId = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    effectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    repealedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_normRevision", x => x.id);
                    table.ForeignKey(
                        name: "fK_normRevision_legalNorm_normId",
                        column: x => x.normId,
                        principalSchema: "inspector",
                        principalTable: "legalNorm",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunkRevisionLink",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    normRevisionId = table.Column<int>(type: "integer", nullable: false),
                    chunkId = table.Column<int>(type: "integer", nullable: false),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_chunkRevisionLink", x => x.id);
                    table.ForeignKey(
                        name: "fK_chunkRevisionLink_normRevisions_normRevisionId",
                        column: x => x.normRevisionId,
                        principalSchema: "inspector",
                        principalTable: "normRevision",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "iX_chunkRevisionLink_normRevisionId",
                schema: "inspector",
                table: "chunkRevisionLink",
                column: "normRevisionId");

            migrationBuilder.CreateIndex(
                name: "ixChunkRevisionLinkChunk",
                schema: "inspector",
                table: "chunkRevisionLink",
                column: "chunkId");

            migrationBuilder.CreateIndex(
                name: "ixLegalNormIdentifier",
                schema: "inspector",
                table: "legalNorm",
                column: "identifier");

            migrationBuilder.CreateIndex(
                name: "iX_normDocumentLink_legalNormId",
                schema: "inspector",
                table: "normDocumentLink",
                column: "legalNormId");

            migrationBuilder.CreateIndex(
                name: "ixNormDocumentLinkDocument",
                schema: "inspector",
                table: "normDocumentLink",
                column: "documentId");

            migrationBuilder.CreateIndex(
                name: "ixNormRevisionNormStatus",
                schema: "inspector",
                table: "normRevision",
                columns: new[] { "normId", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunkRevisionLink",
                schema: "inspector");

            migrationBuilder.DropTable(
                name: "normDocumentLink",
                schema: "inspector");

            migrationBuilder.DropTable(
                name: "normRevision",
                schema: "inspector");

            migrationBuilder.DropTable(
                name: "legalNorm",
                schema: "inspector");
        }
    }
}

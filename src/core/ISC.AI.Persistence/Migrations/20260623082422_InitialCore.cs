using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "core");

            migrationBuilder.CreateTable(
                name: "appUser",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    userName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    displayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    isActive = table.Column<bool>(type: "boolean", nullable: false),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    isDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_appUser", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    docType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    source = table.Column<string>(type: "character varying(700)", maxLength: 700, nullable: true),
                    docDate = table.Column<DateOnly>(type: "date", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    divisionId = table.Column<int>(type: "integer", nullable: false),
                    storageUri = table.Column<string>(type: "text", nullable: true),
                    contentHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_document", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "indexingJob",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    documentId = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    completedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_indexingJob", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clearance",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    userId = table.Column<int>(type: "integer", nullable: false),
                    maxClassification = table.Column<short>(type: "smallint", nullable: false),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    isDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_clearance", x => x.id);
                    table.ForeignKey(
                        name: "fK_clearance_appUser_userId",
                        column: x => x.userId,
                        principalSchema: "core",
                        principalTable: "appUser",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunk",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    documentId = table.Column<int>(type: "integer", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    divisionId = table.Column<int>(type: "integer", nullable: false),
                    isCurrent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_chunk", x => x.id);
                    table.ForeignKey(
                        name: "fK_chunk_documents_documentId",
                        column: x => x.documentId,
                        principalSchema: "core",
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ixAppUserUsername",
                schema: "core",
                table: "appUser",
                column: "userName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ixChunkClassificationDivision",
                schema: "core",
                table: "chunk",
                columns: new[] { "classification", "divisionId" });

            migrationBuilder.CreateIndex(
                name: "ixChunkDocumentOrdinal",
                schema: "core",
                table: "chunk",
                columns: new[] { "documentId", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "ixClearanceUser",
                schema: "core",
                table: "clearance",
                column: "userId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ixDocumentClassificationDivision",
                schema: "core",
                table: "document",
                columns: new[] { "classification", "divisionId" });

            migrationBuilder.CreateIndex(
                name: "ixDocumentContentHash",
                schema: "core",
                table: "document",
                column: "contentHash");

            migrationBuilder.CreateIndex(
                name: "ixIndexingJobStatus",
                schema: "core",
                table: "indexingJob",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunk",
                schema: "core");

            migrationBuilder.DropTable(
                name: "clearance",
                schema: "core");

            migrationBuilder.DropTable(
                name: "indexingJob",
                schema: "core");

            migrationBuilder.DropTable(
                name: "document",
                schema: "core");

            migrationBuilder.DropTable(
                name: "appUser",
                schema: "core");
        }
    }
}

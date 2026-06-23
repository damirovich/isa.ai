using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoreEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "embedding",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    chunkId = table.Column<int>(type: "integer", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(768)", nullable: false),
                    modelKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    divisionId = table.Column<int>(type: "integer", nullable: false),
                    isCurrent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    createdAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pK_embedding", x => x.id);
                    table.ForeignKey(
                        name: "fK_embedding_chunk_chunkId",
                        column: x => x.chunkId,
                        principalSchema: "core",
                        principalTable: "chunk",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "iX_embedding_chunkId",
                schema: "core",
                table: "embedding",
                column: "chunkId");

            migrationBuilder.CreateIndex(
                name: "ixEmbeddingClassificationDivision",
                schema: "core",
                table: "embedding",
                columns: new[] { "classification", "divisionId" });

            migrationBuilder.CreateIndex(
                name: "ixEmbeddingVectorHnsw",
                schema: "core",
                table: "embedding",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "embedding",
                schema: "core");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}

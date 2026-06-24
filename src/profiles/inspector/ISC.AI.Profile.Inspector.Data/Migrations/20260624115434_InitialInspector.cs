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
                name: "legal_norm",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    identifier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_norm", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "norm_document_link",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    legal_norm_id = table.Column<int>(type: "integer", nullable: false),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_norm_document_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_norm_document_link_legal_norm_legal_norm_id",
                        column: x => x.legal_norm_id,
                        principalSchema: "inspector",
                        principalTable: "legal_norm",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "norm_revision",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    norm_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    repealed_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_norm_revision", x => x.id);
                    table.ForeignKey(
                        name: "fk_norm_revision_legal_norm_norm_id",
                        column: x => x.norm_id,
                        principalSchema: "inspector",
                        principalTable: "legal_norm",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chunk_revision_link",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    norm_revision_id = table.Column<int>(type: "integer", nullable: false),
                    chunk_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chunk_revision_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_chunk_revision_link_norm_revisions_norm_revision_id",
                        column: x => x.norm_revision_id,
                        principalSchema: "inspector",
                        principalTable: "norm_revision",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_chunk_revision_link_chunk_id",
                schema: "inspector",
                table: "chunk_revision_link",
                column: "chunk_id");

            migrationBuilder.CreateIndex(
                name: "ix_chunk_revision_link_norm_revision_id",
                schema: "inspector",
                table: "chunk_revision_link",
                column: "norm_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_legal_norm_identifier",
                schema: "inspector",
                table: "legal_norm",
                column: "identifier");

            migrationBuilder.CreateIndex(
                name: "ix_norm_document_link_document_id",
                schema: "inspector",
                table: "norm_document_link",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_norm_document_link_legal_norm_id",
                schema: "inspector",
                table: "norm_document_link",
                column: "legal_norm_id");

            migrationBuilder.CreateIndex(
                name: "ix_norm_revision_norm_id_status",
                schema: "inspector",
                table: "norm_revision",
                columns: new[] { "norm_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chunk_revision_link",
                schema: "inspector");

            migrationBuilder.DropTable(
                name: "norm_document_link",
                schema: "inspector");

            migrationBuilder.DropTable(
                name: "norm_revision",
                schema: "inspector");

            migrationBuilder.DropTable(
                name: "legal_norm",
                schema: "inspector");
        }
    }
}

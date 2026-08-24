using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMethodRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "method_document",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    artifact_kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    inspection_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    citations_json = table.Column<string>(type: "text", nullable: true),
                    all_citations_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    approved_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_method_document", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_method_document_artifact_kind",
                schema: "inspector",
                table: "method_document",
                column: "artifact_kind");

            migrationBuilder.CreateIndex(
                name: "ix_method_document_classification_created_at",
                schema: "inspector",
                table: "method_document",
                columns: new[] { "classification", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_method_document_status",
                schema: "inspector",
                table: "method_document",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "method_document",
                schema: "inspector");
        }
    }
}

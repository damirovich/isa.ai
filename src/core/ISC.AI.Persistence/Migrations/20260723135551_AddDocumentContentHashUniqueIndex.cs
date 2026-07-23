using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentContentHashUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_content_hash",
                schema: "core",
                table: "document");

            migrationBuilder.CreateIndex(
                name: "ix_document_content_hash",
                schema: "core",
                table: "document",
                column: "content_hash",
                unique: true,
                filter: "content_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_content_hash",
                schema: "core",
                table: "document");

            migrationBuilder.CreateIndex(
                name: "ix_document_content_hash",
                schema: "core",
                table: "document",
                column: "content_hash");
        }
    }
}

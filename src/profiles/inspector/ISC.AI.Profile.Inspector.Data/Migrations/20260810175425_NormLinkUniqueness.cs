using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormLinkUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_norm_document_link_legal_norm_id",
                schema: "inspector",
                table: "norm_document_link");

            migrationBuilder.DropIndex(
                name: "ix_chunk_revision_link_norm_revision_id",
                schema: "inspector",
                table: "chunk_revision_link");

            migrationBuilder.CreateIndex(
                name: "ix_norm_document_link_legal_norm_id_document_id",
                schema: "inspector",
                table: "norm_document_link",
                columns: new[] { "legal_norm_id", "document_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chunk_revision_link_norm_revision_id_chunk_id",
                schema: "inspector",
                table: "chunk_revision_link",
                columns: new[] { "norm_revision_id", "chunk_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_norm_document_link_legal_norm_id_document_id",
                schema: "inspector",
                table: "norm_document_link");

            migrationBuilder.DropIndex(
                name: "ix_chunk_revision_link_norm_revision_id_chunk_id",
                schema: "inspector",
                table: "chunk_revision_link");

            migrationBuilder.CreateIndex(
                name: "ix_norm_document_link_legal_norm_id",
                schema: "inspector",
                table: "norm_document_link",
                column: "legal_norm_id");

            migrationBuilder.CreateIndex(
                name: "ix_chunk_revision_link_norm_revision_id",
                schema: "inspector",
                table: "chunk_revision_link",
                column: "norm_revision_id");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CorpusCatalogIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Поиск по заголовку в каталоге НПА — ILIKE '%…%': btree бесполезен, нужен trigram-GIN
            // (pg_trgm — contrib-модуль, есть в образе pgvector и в штатном Postgres).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_document_title_trgm ON core.document USING gin (title gin_trgm_ops);");

            migrationBuilder.CreateIndex(
                name: "ix_document_doc_date",
                schema: "core",
                table: "document",
                column: "doc_date");

            migrationBuilder.CreateIndex(
                name: "ix_document_doc_type",
                schema: "core",
                table: "document",
                column: "doc_type");

            migrationBuilder.CreateIndex(
                name: "ix_chunk_document_id_is_current",
                schema: "core",
                table: "chunk",
                columns: new[] { "document_id", "is_current" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Расширение pg_trgm намеренно не удаляется: им могут пользоваться другие.
            migrationBuilder.Sql("DROP INDEX IF EXISTS core.ix_document_title_trgm;");

            migrationBuilder.DropIndex(
                name: "ix_document_doc_date",
                schema: "core",
                table: "document");

            migrationBuilder.DropIndex(
                name: "ix_document_doc_type",
                schema: "core",
                table: "document");

            migrationBuilder.DropIndex(
                name: "ix_chunk_document_id_is_current",
                schema: "core",
                table: "chunk");
        }
    }
}

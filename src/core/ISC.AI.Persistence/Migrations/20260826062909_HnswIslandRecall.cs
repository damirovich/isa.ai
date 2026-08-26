using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <summary>
    /// Пересборка HNSW-индекса эмбеддингов с ef_construction=512 (ТБ-022). Дефолтная сборка (64)
    /// теряет связность графа с малыми семантическими «островами»: документ есть в корпусе, точный
    /// скан его находит, индексный — нет (инцидент 26.08.2026, воспроизведён на копии данных).
    /// Работает только пара: ef_construction=512 при сборке + hnsw.ef_search=200 при поиске
    /// (второе выставляет PgVectorRetriever). Подробности: docs/reference/pgvector-hnsw-recall.md.
    /// </summary>
    /// <remarks>
    /// DROP INDEX — через IF EXISTS: на дев-сервере слепой индекс уже снят вручную в ходе инцидента.
    /// На большом корпусе (миллионы векторов) перед накаткой поднять maintenance_work_mem по размеру
    /// графа, иначе сборка уйдёт в дисковую фазу и затянется на порядки (см. тот же документ).
    /// </remarks>
    public partial class HnswIslandRecall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Не DropIndex: индекс на дев-сервере уже отсутствует (снят при диагностике инцидента).
            migrationBuilder.Sql("DROP INDEX IF EXISTS core.ix_embedding_embedding;");

            migrationBuilder.CreateIndex(
                name: "ix_embedding_embedding",
                schema: "core",
                table: "embedding",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" })
                .Annotation("Npgsql:StorageParameter:ef_construction", 512)
                .Annotation("Npgsql:StorageParameter:m", 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS core.ix_embedding_embedding;");

            migrationBuilder.CreateIndex(
                name: "ix_embedding_embedding",
                schema: "core",
                table: "embedding",
                column: "embedding")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });
        }
    }
}

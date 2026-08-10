using System.Data.Common;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Интеграционные тесты слоя данных ядра на НАСТОЯЩЕМ PostgreSQL через Testcontainers
/// (EF InMemory не используется — ТО-прог-07). Проверяют, что миграция <c>InitialCore</c>
/// создаёт схему <c>core</c> и таблицы, а обязательные режимные метаданные сохраняются.
/// </summary>
/// <remarks>Требуется запущенный Docker. Образ pgvector/pgvector — готов и под Э3-02 (vector).</remarks>
public sealed class CoreSchemaMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    private CoreDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npg =>
            {
                npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
                npg.UseVector(); // InitialCore включает vector-колонку эмбеддинга
            })
            .UseSnakeCaseNamingConvention()
            .Options);

    [Fact(DisplayName = "Миграция InitialCore создаёт схему core и сохраняет документ")]
    public async Task InitialCore_migration_creates_schema_and_persists_document()
    {
        await using var db = CreateContext();

        await db.Database.MigrateAsync();

        // Раунд-трип: документ с обязательными грифом и подразделением сохраняется и читается.
        db.Documents.Add(new DocumentEntity
        {
            DocType = "приказ",
            Title = "Тестовый документ",
            Classification = 1,
            DivisionId = 7,
        });
        await db.SaveChangesAsync();

        var saved = await db.Documents.SingleAsync();
        saved.Id.ShouldBeGreaterThan(0);
        saved.Classification.ShouldBe<short>(1);
        saved.DivisionId.ShouldBe(7);
    }

    [Fact(DisplayName = "Столбцы грифа и подразделения — NOT NULL (опора fail-closed)")]
    public async Task Regime_columns_are_not_null()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        // Прямой SQL мимо EF: вставка без classification/division_id нарушает NOT NULL.
        var insertWithoutRegime = async () => await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO core.document (doc_type, title, created_at) VALUES ('приказ', 'Без грифа', now())");

        await insertWithoutRegime.ShouldThrowAsync<DbException>();
    }
}

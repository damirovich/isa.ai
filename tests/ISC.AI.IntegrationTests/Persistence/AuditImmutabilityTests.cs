using System.Data.Common;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Интеграционные тесты неизменяемого аудита (ТБ-030/031) на настоящем PostgreSQL (Testcontainers):
/// записи связываются в хеш-цепочку, а UPDATE/DELETE отклоняются append-only триггером БД.
/// </summary>
/// <remarks>Требуется запущенный Docker (образ pgvector/pgvector — InitialCore включает расширение vector).</remarks>
public sealed class AuditImmutabilityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    // Фабрика контекста для AuditWriter с теми же опциями, что в проде (snake_case + pgvector).
    private sealed class TestContextFactory(string connectionString) : IDbContextFactory<CoreDbContext>
    {
        public CoreDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(connectionString, npg =>
                {
                    npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
                    npg.UseVector();
                })
                .UseSnakeCaseNamingConvention()
                .Options);
    }

    [Fact(DisplayName = "Аудит: записи добавляются и связываются в хеш-цепочку (ТБ-031)")]
    public async Task Audit_records_are_chained()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var writer = new AuditWriter(factory);
        await writer.WriteAsync(new AuditEntry(AuditAction.Search, Classification: 1, SubjectId: 7, ObjectRef: "q1"));
        await writer.WriteAsync(new AuditEntry(AuditAction.Generate, Classification: 2, SubjectId: 7, ObjectRef: "doc1"));

        await using var read = factory.CreateDbContext();
        var records = await read.AuditRecords.OrderBy(r => r.Id).ToListAsync();

        records.Count.ShouldBe(2);
        records[0].PrevHash.ShouldBe(new byte[32]);             // генезис — нули
        records[0].RecordHash.Length.ShouldBe(32);              // SHA-256
        records[1].PrevHash.ShouldBe(records[0].RecordHash);    // звено цепочки
    }

    [Fact(DisplayName = "Аудит: record_hash пересчитывается из ПРОЧИТАННЫХ из БД полей и совпадает (обнаружение подмены, ТБ-031)")]
    public async Task Record_hash_recomputes_from_persisted_fields()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var writer = new AuditWriter(factory);
        await writer.WriteAsync(new AuditEntry(AuditAction.Search, Classification: 1, SubjectId: 7, ObjectRef: "q1"));
        await writer.WriteAsync(
            new AuditEntry(AuditAction.Generate, Classification: 2, SubjectId: 7, ObjectRef: "doc1",
                DivisionId: 5, PayloadSensitive: "запрос/ответ"));

        await using var read = factory.CreateDbContext();
        var records = await read.AuditRecords.OrderBy(r => r.Id).ToListAsync();

        // Обнаружение подмены (ТБ-031): пересчёт record_hash из значений, ВЕРНУВШИХСЯ ИЗ БД, должен совпасть
        // с сохранённым. Ловит рассинхрон точности времени (µs в БД vs 100 нс в DateTime) — до фикса не сходилось.
        var previousHash = new byte[32];
        foreach (var record in records)
        {
            record.PrevHash.ShouldBe(previousHash);
            AuditWriter.ComputeRecordHash(previousHash, record).ShouldBe(record.RecordHash);
            previousHash = record.RecordHash;
        }

        // Контроль: точность времени в БД — микросекунды (нет sub-µs остатка), значит хеш воспроизводим.
        records.ShouldAllBe(r => r.OccurredAt.Ticks % TimeSpan.TicksPerMicrosecond == 0);
    }

    [Fact(DisplayName = "Аудит append-only: UPDATE и DELETE проваливаются (ТБ-031)")]
    public async Task Audit_is_append_only()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();

        await new AuditWriter(factory).WriteAsync(new AuditEntry(AuditAction.View, Classification: 1));

        var update = async () => await db.Database.ExecuteSqlRawAsync("UPDATE core.audit_record SET object_ref = 'x'");
        var delete = async () => await db.Database.ExecuteSqlRawAsync("DELETE FROM core.audit_record");

        await update.ShouldThrowAsync<DbException>();
        await delete.ShouldThrowAsync<DbException>();
    }
}

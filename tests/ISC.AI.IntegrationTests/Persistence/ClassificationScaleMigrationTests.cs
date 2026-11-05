using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Миграции ClassificationScaleFiveLevels (ADR-0030): значения прежней шкалы 0–9 выше 4 приводятся к 4 во всех
/// таблицах схемы, включая допуски, а неизменяемый журнал аудита остаётся нетронутым. Значения в пределах
/// шкалы не меняются.
/// </summary>
public sealed class ClassificationScaleMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Ядро: допуск прежней шкалы 9 → «Особой важности» (4), допуск 2 не меняется")]
    public async Task Core_clamps_clearances()
    {
        var core = new CoreContextFactory(_postgres.GetConnectionString());

        await using (var db = core.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260826062909_HnswIslandRecall");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO core.app_user (user_name, is_active, created_at, is_deleted, must_change_password) VALUES "
                + "('admin', true, now(), false, false), ('user', true, now(), false, false)");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO core.clearance (user_id, max_classification, created_at, is_deleted, division_scope) "
                + "SELECT id, CASE user_name WHEN 'admin' THEN 9 ELSE 2 END, now(), false, ARRAY[5] FROM core.app_user");
            await db.Database.MigrateAsync();
        }

        await using (var db = core.CreateDbContext())
        {
            var clearances = await db.Clearances.AsNoTracking().OrderBy(c => c.MaxClassification).Select(c => c.MaxClassification).ToListAsync();
            clearances.ShouldBe([(short)2, ClassificationLevels.Max]);
        }
    }

    [Fact(DisplayName = "Журнал аудита: запись с грифом 9 после миграции ядра остаётся 9 (неизменяемость ТБ-030)")]
    public async Task Audit_records_are_not_rewritten()
    {
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = core.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260826062909_HnswIslandRecall");
        }

        // Запись прежнего писателя — прямой вставкой по формуле хеша версии 1: на этой миграции у журнала ещё нет столбцов
        // ADR-0066 (исход, адрес, сеанс), а нынешняя модель EF их знает.
        var now = System.DateTime.UtcNow;
        var occurredAt = new System.DateTime(now.Ticks - (now.Ticks % System.TimeSpan.TicksPerMicrosecond), System.DateTimeKind.Utc);
        var legacy = new ISC.AI.Persistence.Entities.AuditRecordEntity
        {
            OccurredAt = occurredAt,
            SubjectId = 7,
            Action = ISC.AI.Abstractions.Audit.AuditAction.View,
            ObjectRef = "legacy",
            Classification = 9,
            DivisionId = 5,
            HashVersion = 1,
        };
        var genesis = new byte[32];
        var hash = ISC.AI.Persistence.Audit.AuditWriter.ComputeRecordHash(genesis, legacy);
        await using (var db = core.CreateDbContext())
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO core.audit_record (occurred_at, subject_id, action, object_ref, classification, division_id, payload_sensitive, prev_hash, record_hash)
                VALUES ({occurredAt}, {7}, {(int)ISC.AI.Abstractions.Audit.AuditAction.View}, {"legacy"}, {(short)9}, {5}, {(string?)null}, {genesis}, {hash})
                """);
        }

        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = core.CreateDbContext())
        {
            var record = await db.AuditRecords.AsNoTracking().SingleAsync();
            record.Classification.ShouldBe((short)9);

            // Ни шкала грифов, ни контекст записи (ADR-0066) запись не переписали: версия 1, её хеш сходится.
            record.HashVersion.ShouldBe((short)1);
            ISC.AI.Persistence.Audit.AuditWriter.ComputeRecordHash(record.PrevHash, record).ShouldBe(record.RecordHash);
        }
    }

    [Fact(DisplayName = "«Следствие»: дело и фигурант с грифом 7 → 4, с грифом 3 — без изменений")]
    public async Task Investigation_clamps_its_schema()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());

        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260928152712_IntersectionReviews");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO investigation.case_file (number, title, kind, opened_at, division_id, classification, status, created_at) VALUES "
                + "('УД-7', 'Высокий гриф', 1, DATE '2026-09-01', 5, 7, 1, now()), "
                + "('УД-3', 'Совсекретно', 1, DATE '2026-09-01', 5, 3, 1, now())");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO investigation.person (case_id, display_name, is_unidentified, role, classification, division_id, created_at) "
                + "SELECT id, 'Иванов', false, 3, classification, 5, now() FROM investigation.case_file");

            await db.Database.MigrateAsync();
        }

        await using (var db = factory.CreateDbContext())
        {
            (await db.Cases.AsNoTracking().OrderBy(c => c.Number).Select(c => c.Classification).ToListAsync())
                .ShouldBe([(short)3, ClassificationLevels.Max]);
            (await db.Persons.AsNoTracking().Select(p => p.Classification).OrderBy(c => c).ToListAsync())
                .ShouldBe([(short)3, ClassificationLevels.Max]);
            (await db.Persons.AsNoTracking().Select(p => p.Role).Distinct().ToListAsync()).ShouldBe([PersonRole.Other]);
        }
    }
}

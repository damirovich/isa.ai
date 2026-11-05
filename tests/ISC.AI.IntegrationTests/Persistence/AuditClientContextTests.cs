using System.Data.Common;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Контекст записи журнала аудита (ТБ-080, ADR-0066) на настоящем PostgreSQL: исход, IP-адрес и сеанс в записи и в хеше
/// версии 2; записи до обновления проверяются по версии 1, цепочка сквозная; подмена новых полей рвёт её; откат
/// миграции при записях версии 2 запрещён. Требуется Docker.
/// </summary>
public sealed class AuditClientContextTests : IAsyncLifetime
{
    // Миграция ядра, предшествующая AuditClientContext: на ней журнал ещё без исхода, адреса и сеанса.
    private const string MigrationBefore = "20261102113558_AccountLockout";

    private static readonly Guid Session = Guid.Parse("4f1c2b7e-0d3a-4c55-9e21-7a6b5c4d3e2f");

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ТБ-080: адрес и сеанс — из контекста субъекта, явные значения сценария важнее; всё входит в хеш версии 2")]
    public async Task Writer_takes_address_and_session_from_subject_context()
    {
        var factory = await MigratedAsync();
        var subject = Substitute.For<ISubjectProvider>();
        subject.GetCurrentSessionIdAsync(Arg.Any<CancellationToken>()).Returns(Session);
        subject.GetCurrentClientAddressAsync(Arg.Any<CancellationToken>()).Returns("10.20.30.40");
        var writer = new AuditWriter(factory, subject);

        await writer.WriteAsync(new AuditEntry(AuditAction.Search, 1, SubjectId: 7, ObjectRef: "case:4", Outcome: AuditOutcome.Denied));
        var explicitSession = Guid.NewGuid();
        await writer.WriteAsync(new AuditEntry(
            AuditAction.Login, 0, SubjectId: 7, ObjectRef: "login=ivanov;result=granted",
            ClientAddress: "10.0.0.9", SessionId: explicitSession));

        var records = await ReadAllAsync(factory);
        records.Count.ShouldBe(2);

        records[0].Outcome.ShouldBe(AuditOutcome.Denied);
        records[0].ClientAddress.ShouldBe("10.20.30.40");
        records[0].SessionId.ShouldBe(Session);
        records[0].HashVersion.ShouldBe((short)2);

        records[1].Outcome.ShouldBe(AuditOutcome.Success);
        records[1].ClientAddress.ShouldBe("10.0.0.9");
        records[1].SessionId.ShouldBe(explicitSession);

        VerifyChain(records);
    }

    [Fact(DisplayName = "ТБ-080: сбой контекста субъекта не срывает запись — она пишется без адреса и сеанса")]
    public async Task Subject_context_failure_does_not_block_the_record()
    {
        var factory = await MigratedAsync();
        var subject = Substitute.For<ISubjectProvider>();
        subject.GetCurrentSessionIdAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("нет состояния"));
        subject.GetCurrentClientAddressAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("нет соединения"));

        await new AuditWriter(factory, subject).WriteAsync(new AuditEntry(AuditAction.View, 0, SubjectId: 7, ObjectRef: "doc:1"));

        var record = (await ReadAllAsync(factory)).ShouldHaveSingleItem();
        record.ClientAddress.ShouldBeNull();
        record.SessionId.ShouldBeNull();
        record.Outcome.ShouldBe(AuditOutcome.Success);
        VerifyChain([record]);
    }

    [Fact(DisplayName = "ТБ-031/080: подмена исхода, адреса или сеанса записи версии 2 обнаруживается пересчётом хеша")]
    public async Task Tampering_with_new_fields_breaks_the_chain()
    {
        var factory = await MigratedAsync();
        var subject = Substitute.For<ISubjectProvider>();
        subject.GetCurrentSessionIdAsync(Arg.Any<CancellationToken>()).Returns(Session);
        subject.GetCurrentClientAddressAsync(Arg.Any<CancellationToken>()).Returns("10.20.30.40");
        await new AuditWriter(factory, subject).WriteAsync(
            new AuditEntry(AuditAction.Export, 1, SubjectId: 7, ObjectRef: "case:4", Outcome: AuditOutcome.Error));

        var stored = (await ReadAllAsync(factory)).ShouldHaveSingleItem();
        AuditWriter.ComputeRecordHash(stored.PrevHash, stored).ShouldBe(stored.RecordHash);

        // Каждое новое поле — в хеше: правка любого из них в базе (в обход триггера) видна проверкой цепочки.
        AuditWriter.ComputeRecordHash(stored.PrevHash, Copy(stored, r => r.Outcome = AuditOutcome.Success)).ShouldNotBe(stored.RecordHash);
        AuditWriter.ComputeRecordHash(stored.PrevHash, Copy(stored, r => r.ClientAddress = "10.20.30.41")).ShouldNotBe(stored.RecordHash);
        AuditWriter.ComputeRecordHash(stored.PrevHash, Copy(stored, r => r.SessionId = Guid.NewGuid())).ShouldNotBe(stored.RecordHash);
        AuditWriter.ComputeRecordHash(stored.PrevHash, Copy(stored, r => r.SessionId = null)).ShouldNotBe(stored.RecordHash);

        // Понижение версии записи до 1 тоже не проходит: формула версии 1 даёт другой хеш.
        AuditWriter.ComputeRecordHash(stored.PrevHash, Copy(stored, r => r.HashVersion = 1)).ShouldNotBe(stored.RecordHash);
    }

    [Fact(DisplayName = "ADR-0066: в версии 2 сдвиг границ полей через «|» меняет хеш (в версии 1 — нет)")]
    public void Field_boundaries_are_unambiguous_in_version_2()
    {
        var at = new DateTime(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

        // «|» в ссылке на объект и в сводке позволяет в версии 1 переписать гриф и подразделение, не меняя хеша:
        // обе записи дают канон «…|doc:1|3|5|0||p».
        var a = new AuditRecordEntity
        {
            OccurredAt = at, Action = AuditAction.View, ObjectRef = "doc:1|3|5", Classification = 0, DivisionId = null, PayloadSensitive = "p",
        };
        var b = new AuditRecordEntity
        {
            OccurredAt = at, Action = AuditAction.View, ObjectRef = "doc:1", Classification = 3, DivisionId = 5, PayloadSensitive = "0||p",
        };
        var genesis = new byte[32];

        // Слабость версии 1, которую закрывает версия 2: разные записи — один хеш.
        a.HashVersion = b.HashVersion = 1;
        AuditWriter.ComputeRecordHash(genesis, a).ShouldBe(AuditWriter.ComputeRecordHash(genesis, b));

        a.HashVersion = b.HashVersion = 2;
        a.Outcome = b.Outcome = AuditOutcome.Success;
        AuditWriter.ComputeRecordHash(genesis, a).ShouldNotBe(AuditWriter.ComputeRecordHash(genesis, b));

        // Пустое поле и поле «-» тоже не путаются.
        var empty = new AuditRecordEntity { OccurredAt = at, Action = AuditAction.View, ObjectRef = "", HashVersion = 2 };
        var dash = new AuditRecordEntity { OccurredAt = at, Action = AuditAction.View, ObjectRef = "-", HashVersion = 2 };
        var missing = new AuditRecordEntity { OccurredAt = at, Action = AuditAction.View, ObjectRef = null, HashVersion = 2 };
        new[] { empty, dash, missing }.Select(r => Convert.ToHexString(AuditWriter.ComputeRecordHash(genesis, r)))
            .Distinct().Count().ShouldBe(3);
    }

    [Fact(DisplayName = "ТБ-080: записи до обновления — версии 1 без исхода, адреса и сеанса, проверяются по ней; цепочка с новыми записями сквозная")]
    public async Task Records_before_the_update_stay_version_1_and_the_chain_continues()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
        }

        // Две записи «старого» журнала — так, как их писал прежний писатель: формула версии 1, без новых столбцов.
        var first = await InsertLegacyAsync(factory, new byte[32], AuditAction.Login, subjectId: 7, objectRef: "login=ivanov;result=granted");
        await InsertLegacyAsync(factory, first, AuditAction.View, subjectId: 7, objectRef: "doc:1", payload: "просмотр | сводка");

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var subject = Substitute.For<ISubjectProvider>();
        subject.GetCurrentSessionIdAsync(Arg.Any<CancellationToken>()).Returns(Session);
        subject.GetCurrentClientAddressAsync(Arg.Any<CancellationToken>()).Returns("10.20.30.40");
        await new AuditWriter(factory, subject).WriteAsync(new AuditEntry(AuditAction.View, 0, SubjectId: 7, ObjectRef: "doc:2"));

        var records = await ReadAllAsync(factory);
        records.Count.ShouldBe(3);
        records.Take(2).ShouldAllBe(r => r.HashVersion == 1 && r.Outcome == null && r.ClientAddress == null && r.SessionId == null);
        records[2].HashVersion.ShouldBe((short)2);
        records[2].PrevHash.ShouldBe(records[1].RecordHash);
        VerifyChain(records);
    }

    [Fact(DisplayName = "ТБ-031: откат миграции AuditClientContext при записях версии 2 запрещён; на журнале без них — проходит")]
    public async Task Rollback_is_refused_once_version_2_records_exist()
    {
        var factory = await MigratedAsync();

        // Журнал без записей версии 2 — откат и повторный накат проходят (стенд разработчика).
        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            await db.Database.MigrateAsync();
        }

        await new AuditWriter(factory).WriteAsync(new AuditEntry(AuditAction.View, 0, SubjectId: 7, ObjectRef: "doc:1"));

        await using (var db = factory.CreateDbContext())
        {
            var rollback = async () => await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            await rollback.ShouldThrowAsync<DbException>();
        }

        // Журнал цел: запись на месте, её контекст и хеш проверяются.
        var record = (await ReadAllAsync(factory)).ShouldHaveSingleItem();
        record.HashVersion.ShouldBe((short)2);
        VerifyChain([record]);
    }

    private async Task<CoreContextFactory> MigratedAsync()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        return factory;
    }

    private static async Task<List<AuditRecordEntity>> ReadAllAsync(CoreContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        return await db.AuditRecords.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    // Проверка цепочки: каждое звено ссылается на предыдущее, хеш пересчитывается из прочитанных полей по формуле ВЕРСИИ записи.
    private static void VerifyChain(IReadOnlyList<AuditRecordEntity> records)
    {
        var previous = new byte[32];
        foreach (var record in records)
        {
            record.PrevHash.ShouldBe(previous);
            AuditWriter.ComputeRecordHash(previous, record).ShouldBe(record.RecordHash);
            previous = record.RecordHash;
        }
    }

    // Запись прежнего писателя — прямой вставкой: на миграции до AuditClientContext новых столбцов нет, а модель EF их знает.
    private static async Task<byte[]> InsertLegacyAsync(
        CoreContextFactory factory, byte[] previousHash, AuditAction action, int? subjectId, string? objectRef, string? payload = null)
    {
        var occurredAt = DateTime.UtcNow;
        occurredAt = new DateTime(occurredAt.Ticks - (occurredAt.Ticks % TimeSpan.TicksPerMicrosecond), DateTimeKind.Utc);
        var legacy = new AuditRecordEntity
        {
            OccurredAt = occurredAt,
            SubjectId = subjectId,
            Action = action,
            ObjectRef = objectRef,
            Classification = 0,
            PayloadSensitive = payload,
            HashVersion = 1,
        };
        var hash = AuditWriter.ComputeRecordHash(previousHash, legacy);

        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO core.audit_record (occurred_at, subject_id, action, object_ref, classification, division_id, payload_sensitive, prev_hash, record_hash)
            VALUES ({occurredAt}, {subjectId}, {(int)action}, {objectRef}, {(short)0}, {(int?)null}, {payload}, {previousHash}, {hash})
            """);
        return hash;
    }

    private static AuditRecordEntity Copy(AuditRecordEntity source, Action<AuditRecordEntity> change)
    {
        var copy = new AuditRecordEntity
        {
            Id = source.Id,
            OccurredAt = source.OccurredAt,
            SubjectId = source.SubjectId,
            Action = source.Action,
            ObjectRef = source.ObjectRef,
            Classification = source.Classification,
            DivisionId = source.DivisionId,
            PayloadSensitive = source.PayloadSensitive,
            Outcome = source.Outcome,
            ClientAddress = source.ClientAddress,
            SessionId = source.SessionId,
            HashVersion = source.HashVersion,
            PrevHash = source.PrevHash,
            RecordHash = source.RecordHash,
        };
        change(copy);
        return copy;
    }
}

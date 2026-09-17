using System;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Разрешение слабых ссылок по RegNumber (<see cref="DocumentLookup"/>) на настоящем PostgreSQL.
/// Требуется Docker.
/// </summary>
/// <remarks>
/// Главная проверка — РЕШЁТКА ДОСТУПА (ТБ-020/021): порт зовут чужие модули (архив проверок
/// профиля), и без фильтра в запросе он стал бы обходным каналом чтения метаданных документов,
/// которых субъект не видит в списке. Недоступный документ в ответе обязан ОТСУТСТВОВАТЬ.
/// </remarks>
public sealed class DocumentLookupTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Разрешение номеров не отдаёт документы выше допуска или чужого подразделения")]
    public async Task Lookup_never_exceeds_the_visible_set()
    {
        var lookup = await BuildAsync();

        // Субъект: допуск 0, подразделение 5 — видит только СП-1.
        var limited = new AccessContext("42", 0, [5]);
        var cards = await lookup.ResolveByRegNumbersAsync(
            ["СП-1", "СП-2", "СП-3", "НЕТ-ТАКОГО"], limited);

        cards.Count.ShouldBe(1);
        cards["СП-1"].TypeName.ShouldBe("Справка");
        cards["СП-1"].DivisionId.ShouldBe(5);

        // Полный допуск — все три; несуществующий номер отсутствует, а не падает.
        var full = new AccessContext("42", 9, [5, 9]);
        var all = await lookup.ResolveByRegNumbersAsync(["СП-1", "СП-2", "СП-3", "НЕТ-ТАКОГО"], full);
        all.Count.ShouldBe(3);

        // Пустой вход — пустой словарь без обращения к БД по существу.
        (await lookup.ResolveByRegNumbersAsync([], limited)).ShouldBeEmpty();
    }

    private async Task<DocumentLookup> BuildAsync()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            var type = new DocumentType { Name = "Справка", Group = DocumentGroup.Storage };
            db.DocumentTypes.Add(type);
            await db.SaveChangesAsync();

            // СП-1 — доступна субъекту (гриф 0, подразделение 5); СП-2 — гриф выше допуска;
            // СП-3 — чужое подразделение.
            db.Documents.AddRange(
                NewDocument(type.Id, "СП-1", classification: 0, divisionId: 5),
                NewDocument(type.Id, "СП-2", classification: 5, divisionId: 5),
                NewDocument(type.Id, "СП-3", classification: 0, divisionId: 9));
            await db.SaveChangesAsync();
        }

        return new DocumentLookup(factory, new AllowAllAccessPolicy());
    }

    private static Document NewDocument(int typeId, string regNumber, short classification, int divisionId) => new()
    {
        RegNumber = regNumber,
        RegDate = new DateOnly(2026, 5, 1),
        TypeId = typeId,
        DirectionFlag = DocumentDirection.Internal,
        ShortContent = "Справка по итогам проверки",
        Classification = classification,
        DivisionId = divisionId,
    };
}

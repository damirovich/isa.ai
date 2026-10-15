using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// История единого поиска (ADR-0049, миграция <c>SearchHistory</c>) на настоящем Postgres: повтор того же запроса (регистр и
/// «ё/е» не важны, те же виды и отдел) поднимает строку, а не плодит; другие виды или отдел — отдельная строка; новые
/// первыми, закреплённые — выше всех; незакреплённые сверх 30 вытесняются, закреплённые — нет, их не больше 20; чужая
/// история не видна и не меняется; строка под грифом выше текущего допуска не показывается; удаление и очистка.
/// </summary>
[Trait("Category", "Gate")]
public sealed class InvestigationSearchHistoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "История: повтор поднимает строку, порядок, вытеснение 30, закреплённые до 20, чужое и выше допуска — не видно, удаление и очистка")]
    public async Task History_is_personal_ordered_and_bounded()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        var clock = new SteppingClock();
        var store = new SearchHistoryStore(factory, clock);
        const int me = 10;
        const int colleague = 11;

        await store.RecordAsync(me, Draft("Пётр Иванов", 3));
        await store.RecordAsync(me, Draft("01 KG 123", 1, SearchKinds.Vehicles));
        await store.RecordAsync(me, Draft("ПЕТР ИВАНОВ", 5)); // тот же запрос — строка поднимается, текст и число — новые
        await store.RecordAsync(me, Draft("Пётр Иванов", 2, direction: CaseDirection.Surveillance)); // другой отдел — отдельно
        await store.RecordAsync(colleague, Draft("Сидоров", 1));

        var mine = await store.ListAsync(me, maxClassification: 9);
        mine.Select(r => (r.Text, r.Direction, r.ResultCount)).ShouldBe(
        [
            ("Пётр Иванов", (CaseDirection?)CaseDirection.Surveillance, 2),
            ("ПЕТР ИВАНОВ", (CaseDirection?)null, 5),
            ("01 KG 123", (CaseDirection?)null, 1),
        ]);
        (await store.ListAsync(colleague, 9)).Select(r => r.Text).ShouldBe(["Сидоров"]);

        // Чужую запись не закрепить и не удалить — «не найдено».
        var foreign = (await store.ListAsync(colleague, 9))[0].Id;
        (await store.SetPinnedAsync(me, foreign, true)).ShouldBe(SearchHistoryWriteResult.NotFound);
        (await store.DeleteAsync(me, foreign)).ShouldBe(SearchHistoryWriteResult.NotFound);

        // Закреплённая — выше всех и переживает вытеснение.
        var plate = mine.Single(r => r.Text == "01 KG 123").Id;
        (await store.SetPinnedAsync(me, plate, true)).ShouldBe(SearchHistoryWriteResult.Ok);
        for (var i = 0; i < ISearchHistoryStore.MaxRecent + 5; i++)
        {
            await store.RecordAsync(me, Draft("запрос " + i, i));
        }

        var after = await store.ListAsync(me, 9);
        after[0].Text.ShouldBe("01 KG 123");
        after[0].IsPinned.ShouldBeTrue();
        after.Count(r => !r.IsPinned).ShouldBe(ISearchHistoryStore.MaxRecent);
        after[1].Text.ShouldBe("запрос 34");
        after.ShouldNotContain(r => r.Text == "Пётр Иванов"); // самые старые вытеснены

        // Строка под грифом выше текущего допуска не показывается.
        await store.RecordAsync(me, Draft("секретный запрос", 1) with { Classification = 3 });
        (await store.ListAsync(me, 2)).ShouldNotContain(r => r.Text == "секретный запрос");
        (await store.ListAsync(me, 3)).ShouldContain(r => r.Text == "секретный запрос");

        // Закреплённых — не больше 20.
        var recent = (await store.ListAsync(me, 9)).Where(r => !r.IsPinned).Select(r => r.Id).ToList();
        foreach (var id in recent.Take(ISearchHistoryStore.MaxPinned - 1))
        {
            (await store.SetPinnedAsync(me, id, true)).ShouldBe(SearchHistoryWriteResult.Ok);
        }

        (await store.SetPinnedAsync(me, recent[ISearchHistoryStore.MaxPinned], true)).ShouldBe(SearchHistoryWriteResult.PinLimit);
        (await store.SetPinnedAsync(me, plate, false)).ShouldBe(SearchHistoryWriteResult.Ok);

        // Удаление и очистка: очистка не трогает закреплённые и чужую историю.
        (await store.DeleteAsync(me, plate)).ShouldBe(SearchHistoryWriteResult.Ok);
        var removed = await store.ClearAsync(me);
        removed.ShouldBeGreaterThan(0);
        (await store.ListAsync(me, 9)).ShouldAllBe(r => r.IsPinned);
        (await store.ListAsync(me, 9)).Count.ShouldBe(ISearchHistoryStore.MaxPinned - 1);
        (await store.ListAsync(colleague, 9)).Count.ShouldBe(1);
    }

    private static SearchHistoryDraft Draft(string text, int found, SearchKinds kinds = SearchKinds.All, CaseDirection? direction = null) =>
        new(SearchTerms.Parse(text), kinds, direction, found, Classification: 1);

    /// <summary>Часы, идущие на секунду вперёд при каждом чтении: порядок «новые первыми» предсказуем.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }
}

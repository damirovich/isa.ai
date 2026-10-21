using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Search;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Единый поиск (ТФ-ПСК-01, ADR-0048) без БД: разбор запроса по правилам ТО-мат-11; область — доступные дела (с
/// отделом), остальные виды ищутся только в них; пределы разделов и пометка «показаны первые»; выбор видов; расшифровки —
/// с делом носителя; документы — пачками по пределу порта документооборота; одна запись журнала с искомым текстом
/// (ТБ-032); состояние страницы в адресе и выделение совпадения без учёта регистра и «ё/е».
/// </summary>
public sealed class UnifiedSearchTests
{
    private static readonly AccessContext Access = new("10", 2, [5]);

    private readonly ICaseStore _cases = Substitute.For<ICaseStore>();
    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IPersonRequisiteStore _requisites = Substitute.For<IPersonRequisiteStore>();
    private readonly ILinkEventStore _events = Substitute.For<ILinkEventStore>();
    private readonly ICaseReportStore _reports = Substitute.For<ICaseReportStore>();
    private readonly ICaseDocumentStore _caseDocuments = Substitute.For<ICaseDocumentStore>();
    private readonly IDocumentLookup _documents = Substitute.For<IDocumentLookup>();
    private readonly IMediaCatalog _media = Substitute.For<IMediaCatalog>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly ISearchHistoryStore _history = Substitute.For<ISearchHistoryStore>();
    private CaseFilter? _scopeFilter;

    public UnifiedSearchTests()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(10);
        _cases.ListAsync(Arg.Do<CaseFilter>(f => _scopeFilter = f), Access, Arg.Any<CancellationToken>())
            .Returns(new CasePage([Case(1, "14/24", "Задание № 14/24 — Учебный Марат"), Case(2, "УД-5", "Кража", task: "3/24")], 2));
        _persons.SearchInCasesAsync(Arg.Any<SearchTerms>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns([]);
        _requisites.SearchVehiclesAsync(Arg.Any<SearchTerms>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns([]);
        _requisites.SearchAddressesAsync(Arg.Any<SearchTerms>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns([]);
        _reports.SearchAsync(Arg.Any<SearchTerms>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns([]);
        _caseDocuments.ListLinksAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>()).Returns([]);
        _cases.ListMediaAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    // ---------------- разбор запроса ----------------

    [Fact(DisplayName = "ТО-мат-11: текст — нижний регистр и «ё» → «е» (ө/ү/ң сохраняются); госномер и адрес — нормализованные; дата и год рождения")]
    public void Terms_are_normalized_once()
    {
        var plate = SearchTerms.Parse("  01 кг 123 АВС  ");
        plate.Raw.ShouldBe("01 кг 123 АВС");
        plate.Text.ShouldBe("01 кг 123 авс");
        plate.Plate.ShouldBe(RequisiteNormalizer.Plate("01 кг 123 АВС"));

        SearchTerms.Parse("Пётр Үмөтов").Text.ShouldBe("петр үмөтов");
        SearchTerms.Parse("улица Ленина").Address.ShouldBe(RequisiteNormalizer.Address("ул. Ленина"));
        SearchTerms.Parse("12.03.1985").Date.ShouldBe(new DateOnly(1985, 3, 12));
        SearchTerms.Parse("1985-03-12").Date.ShouldBe(new DateOnly(1985, 3, 12));
        SearchTerms.Parse("1985").Year.ShouldBe(1985);
        SearchTerms.Parse("1800").Year.ShouldBeNull();
        SearchTerms.Parse("12345").Year.ShouldBeNull();
        SearchTerms.Parse("и").IsSearchable.ShouldBeFalse();
        SearchTerms.Parse("   ").IsSearchable.ShouldBeFalse();
        SearchTerms.Parse(new string('я', 500)).Raw.Length.ShouldBe(SearchTerms.MaxLength);
    }

    // ---------------- запрос ----------------

    [Fact(DisplayName = "ТБ-030/032: одна запись «Поиск» с видами, отделом и искомым текстом (в чувствительной части)")]
    public void Audit_summary_has_text_kinds_and_direction()
    {
        var query = new UnifiedSearchQuery("  Иванов  ", SearchKinds.Persons | SearchKinds.Vehicles, CaseDirection.Surveillance);
        query.AuditAction.ShouldBe(AuditAction.Search);
        query.AuditSummary.ShouldBe("investigation:search:kinds=Persons, Vehicles;direction=Surveillance;text=Иванов");
        new UnifiedSearchQuery("ab").AuditSummary.ShouldBe("investigation:search:kinds=All;direction=-;text=ab");
    }

    [Fact(DisplayName = "Область — доступные дела с выбранным отделом; дела — по номеру, № задания и названию; фигуранты и прочее — только в делах области")]
    public async Task Scope_is_accessible_cases_and_cases_match_number_task_title()
    {
        var result = (await HandleAsync(new UnifiedSearchQuery("3/24", Direction: CaseDirection.Establishment))).Data.ShouldNotBeNull();

        _scopeFilter.ShouldNotBeNull().Direction.ShouldBe(CaseDirection.Establishment);
        result.Cases.Select(c => c.Id).ShouldBe([2]); // № задания
        result.CaseNumbers.ShouldBe(new Dictionary<int, string> { [1] = "14/24", [2] = "УД-5" }, ignoreOrder: true);
        await _persons.Received(1).SearchInCasesAsync(
            Arg.Is<SearchTerms>(t => t.Text == "3/24"), Arg.Is<IReadOnlyCollection<int>>(ids => ids.Order().SequenceEqual(new[] { 1, 2 })),
            UnifiedSearchQuery.Limit + 1, Access, Arg.Any<CancellationToken>());

        (await HandleAsync(new UnifiedSearchQuery("учебный"))).Data!.Cases.Select(c => c.Id).ShouldBe([1]); // название
        (await HandleAsync(new UnifiedSearchQuery("уд-5"))).Data!.Cases.Select(c => c.Id).ShouldBe([2]);     // номер, регистр
    }

    [Fact(DisplayName = "Больше предела — первые 50 и пометка «показаны первые» у этого вида; у остальных — нет")]
    public async Task Sections_are_capped_and_flagged()
    {
        _persons.SearchInCasesAsync(Arg.Any<SearchTerms>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, UnifiedSearchQuery.Limit + 1).Select(i => Person(i)).ToList());

        var result = (await HandleAsync(new UnifiedSearchQuery("иванов"))).Data.ShouldNotBeNull();

        result.Persons.Count.ShouldBe(UnifiedSearchQuery.Limit);
        result.Truncated.ShouldBe(SearchKinds.Persons);
        result.TotalCount.ShouldBe(UnifiedSearchQuery.Limit);
    }

    [Theory(DisplayName = "ADR-0055: запрос, похожий на номер, — нормализованный номер (цифры, 996); дата, год, текст и короткие числа номером не считаются")]
    [InlineData("+996 555 12-34-56", "996555123456")]
    [InlineData("0555 123 456", "996555123456")]
    [InlineData("(0555) 123-456", "996555123456")]
    [InlineData("123456", "123456")]
    [InlineData("12.03.1985", null)]
    [InlineData("1985", null)]
    [InlineData("1234", null)]
    [InlineData("Иванов 555123", null)]
    public void Phone_terms_are_normalized(string raw, string? expected) =>
        SearchTerms.Parse(raw).Phone.ShouldBe(expected);

    [Fact(DisplayName = "ADR-0055: телефоны — свой вид выдачи: ищутся по разобранному номеру, входят в общее число и в предел")]
    public async Task Phones_are_searched_as_own_kind()
    {
        _requisites.SearchPhonesAsync(Arg.Any<SearchTerms>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns([new PhoneSearchRow(7, 1, "Иванов", new PersonPhoneRow(3, 7, PhoneKind.Mobile, "+996 555 123 456", null))]);

        var result = (await HandleAsync(new UnifiedSearchQuery("0555 123 456", SearchKinds.Phones))).Data.ShouldNotBeNull();

        result.Phones.ShouldHaveSingleItem().Phone.Number.ShouldBe("+996 555 123 456");
        result.TotalCount.ShouldBe(1);
        await _requisites.Received(1).SearchPhonesAsync(
            Arg.Is<SearchTerms>(t => t.Phone == "996555123456"), Arg.Any<IReadOnlyCollection<int>>(), UnifiedSearchQuery.Limit + 1,
            Access, Arg.Any<CancellationToken>());
        await _persons.DidNotReceiveWithAnyArgs().SearchInCasesAsync(default!, default!, default, default!, default);
        SearchKinds.All.HasFlag(SearchKinds.Phones).ShouldBeTrue();
    }

    [Fact(DisplayName = "ADR-0056: события — свой вид выдачи: ищутся хранилищем событий в делах области, входят в общее число и в предел")]
    public async Task Events_are_searched_as_own_kind()
    {
        _events.SearchAsync(Arg.Any<SearchTerms>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns([new EventSearchRow(5, LinkEventKind.Meeting, "Встреча у рынка", null, 7, "Иванов", 1, "водитель", "г. Ош", null)]);

        var result = (await HandleAsync(new UnifiedSearchQuery("рынок", SearchKinds.Events))).Data.ShouldNotBeNull();

        result.Events.ShouldHaveSingleItem().Title.ShouldBe("Встреча у рынка");
        result.TotalCount.ShouldBe(1);
        await _events.Received(1).SearchAsync(
            Arg.Is<SearchTerms>(t => t.Raw == "рынок"), Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 2), UnifiedSearchQuery.Limit + 1,
            Access, Arg.Any<CancellationToken>());
        SearchKinds.All.HasFlag(SearchKinds.Events).ShouldBeTrue();

        await HandleAsync(new UnifiedSearchQuery("рынок", SearchKinds.Persons));
        await _events.ReceivedWithAnyArgs(1).SearchAsync(default!, default!, default, default!, default); // не выбран — не ищется
    }

    [Fact(DisplayName = "Выбранные виды: ищется только выбранное — остальные хранилища и порты пакетов не вызываются")]
    public async Task Only_selected_kinds_are_searched()
    {
        await HandleAsync(new UnifiedSearchQuery("иванов", SearchKinds.Persons));

        await _persons.ReceivedWithAnyArgs(1).SearchInCasesAsync(default!, default!, default, default!, default);
        await _requisites.DidNotReceiveWithAnyArgs().SearchVehiclesAsync(default!, default!, default, default!, default);
        await _requisites.DidNotReceiveWithAnyArgs().SearchAddressesAsync(default!, default!, default, default!, default);
        await _requisites.DidNotReceiveWithAnyArgs().SearchPhonesAsync(default!, default!, default, default!, default);
        await _reports.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default, default!, default);
        await _media.DidNotReceiveWithAnyArgs().SearchTranscriptsAsync(default!, default!, default, default!, default);
        await _caseDocuments.DidNotReceiveWithAnyArgs().ListLinksAsync(default!, default!, default);
    }

    [Fact(DisplayName = "Нет доступных дел — ничего не ищется дальше и выдача пуста (недоступное не раскрывается ничем)")]
    public async Task Empty_scope_searches_nothing_else()
    {
        _cases.ListAsync(Arg.Any<CaseFilter>(), Access, Arg.Any<CancellationToken>()).Returns(new CasePage([], 0));

        var result = (await HandleAsync(new UnifiedSearchQuery("иванов"))).Data.ShouldNotBeNull();

        result.TotalCount.ShouldBe(0);
        await _persons.DidNotReceiveWithAnyArgs().SearchInCasesAsync(default!, default!, default, default!, default);
        await _media.DidNotReceiveWithAnyArgs().SearchTranscriptsAsync(default!, default!, default, default!, default);
    }

    [Fact(DisplayName = "Расшифровки: носители дел области → порт каталога «Медиа» (запрос как ввели) → у фрагмента дело носителя")]
    public async Task Transcripts_come_with_case_of_asset()
    {
        _cases.ListMediaAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([500, 501]);
        _media.SearchTranscriptsAsync(Arg.Any<IReadOnlyCollection<int>>(), "Салам", UnifiedSearchQuery.Limit + 1, Access, Arg.Any<CancellationToken>())
            .Returns([new TranscriptHit(501, "запись.mp3", MediaKind.Audio, 3, 65_000, 70_000, "салам дүйнө")]);
        _cases.MapMediaAssetsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [501] = 2 });

        var hit = (await HandleAsync(new UnifiedSearchQuery("Салам", SearchKinds.Transcripts))).Data!.Transcripts.ShouldHaveSingleItem();

        hit.ShouldBe(new TranscriptSearchHit(501, 2, "запись.mp3", MediaKind.Audio, 3, 65_000, "салам дүйнө"));
    }

    [Fact(DisplayName = "Документы дел: карточки — пачками по пределу порта (200); совпадение — рег. номер, вид, краткое содержание; недоступный документ — нет")]
    public async Task Documents_are_resolved_in_batches_and_matched()
    {
        _caseDocuments.ListLinksAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, 250).Select(id => new CaseDocumentRef(2, id)).ToList());
        var batches = new List<int>();
        _documents.ResolveByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<int>>();
                batches.Add(ids.Count);
                // Документ 7 недоступен — порт его не вернул; 210 — подходит по краткому содержанию.
                return (IReadOnlyDictionary<int, DocumentBrief>)ids.Where(id => id != 7).ToDictionary(id => id, id => new DocumentBrief(
                    id, $"Р-{id}", new DateOnly(2026, 10, 1), "Справка", id == 210 ? "О встрече у рынка" : "прочее", null,
                    DocumentAggregatedStatus.Registered, 1));
            });

        var found = (await HandleAsync(new UnifiedSearchQuery("рынка", SearchKinds.Documents))).Data!.Documents;

        batches.ShouldBe([UnifiedSearchQuery.DocumentBatch, 50]);
        found.ShouldBe([new DocumentSearchHit(210, 2, "Р-210", new DateOnly(2026, 10, 1), "Справка", "О встрече у рынка")]);
        (await HandleAsync(new UnifiedSearchQuery("р-7", SearchKinds.Documents))).Data!.Documents
            .Select(d => d.DocumentId).ShouldNotContain(7); // недоступный документ не выдаётся и по номеру
    }

    [Fact(DisplayName = "Предел пачки порта документооборота совпадает с DocumentLookup.MaxBatch — иначе идентификаторы молча терялись бы")]
    public void Document_batch_matches_lookup_limit() =>
        UnifiedSearchQuery.DocumentBatch.ShouldBe(ISC.AI.Modules.DocFlow.Data.DocumentLookup.MaxBatch);

    [Fact(DisplayName = "Короче двух знаков или без видов — отказ до обращения к данным; валидатор говорит то же")]
    public async Task Too_short_or_no_kinds_is_refused()
    {
        (await HandleAsync(new UnifiedSearchQuery(" и "))).StatusMessage.ShouldBe(UnifiedSearchQuery.TooShortMessage);
        (await HandleAsync(new UnifiedSearchQuery("иванов", SearchKinds.None))).StatusMessage.ShouldBe(UnifiedSearchQuery.NoKindsMessage);
        await _accessProvider.DidNotReceiveWithAnyArgs().GetCurrentAsync(default);

        var validator = new UnifiedSearchValidator();
        validator.Validate(new UnifiedSearchQuery("и")).IsValid.ShouldBeFalse();
        validator.Validate(new UnifiedSearchQuery("иванов", SearchKinds.None)).IsValid.ShouldBeFalse();
        validator.Validate(new UnifiedSearchQuery("01KG")).IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "ADR-0049: выполненный поиск записывается в историю вошедшего — запрос, виды, отдел, число найденного и гриф = его допуск; без сотрудника — нет")]
    public async Task Search_is_recorded_to_history_of_subject()
    {
        _persons.SearchInCasesAsync(Arg.Any<SearchTerms>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns([Person(1), Person(2)]);

        await HandleAsync(new UnifiedSearchQuery("  Иванов ", SearchKinds.Persons, CaseDirection.Establishment));

        await _history.Received(1).RecordAsync(
            10,
            Arg.Is<SearchHistoryDraft>(d => d.Terms.Raw == "Иванов" && d.Kinds == SearchKinds.Persons
                && d.Direction == CaseDirection.Establishment && d.ResultCount == 2 && d.Classification == Access.MaxClassification),
            Arg.Any<CancellationToken>());

        _history.ClearReceivedCalls();
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        await HandleAsync(new UnifiedSearchQuery("Иванов"));
        await _history.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default);

        // Отказ по длине — не поиск: в историю не попадает.
        await HandleAsync(new UnifiedSearchQuery("и"));
        await _history.DidNotReceiveWithAnyArgs().RecordAsync(default, default!, default);
    }

    [Fact(DisplayName = "ADR-0049: история — своя и в пределах текущего допуска; закрепление сверх предела — отказ; чужая запись — «не найдено»; журнал — без текста")]
    public async Task History_requests_are_own_and_audited_without_text()
    {
        var rows = new List<SearchHistoryRow> { new(5, "Иванов", SearchKinds.All, null, 3, true, DateTime.UtcNow) };
        _history.ListAsync(10, Access.MaxClassification, Arg.Any<CancellationToken>()).Returns(rows);
        (await new ListSearchHistoryQuery.Handler(_history, _subject, _accessProvider).Handle(new ListSearchHistoryQuery(), CancellationToken.None))
            .Data.ShouldBe(rows);

        _history.SetPinnedAsync(10, 5, true, Arg.Any<CancellationToken>()).Returns(SearchHistoryWriteResult.PinLimit);
        _history.SetPinnedAsync(10, 6, true, Arg.Any<CancellationToken>()).Returns(SearchHistoryWriteResult.NotFound);
        var pin = new PinSearchHistoryCommand.Handler(_history, _subject);
        (await pin.Handle(new PinSearchHistoryCommand(5, true), CancellationToken.None)).StatusMessage.ShouldBe(PinSearchHistoryCommand.PinLimitMessage);
        (await pin.Handle(new PinSearchHistoryCommand(6, true), CancellationToken.None)).StatusMessage.ShouldBe(SearchHistoryMessages.NotFound);

        _history.DeleteAsync(10, 7, Arg.Any<CancellationToken>()).Returns(SearchHistoryWriteResult.Ok);
        (await new DeleteSearchHistoryCommand.Handler(_history, _subject).Handle(new DeleteSearchHistoryCommand(7), CancellationToken.None))
            .Data.ShouldBeTrue();
        _history.ClearAsync(10, Arg.Any<CancellationToken>()).Returns(4);
        (await new ClearSearchHistoryCommand.Handler(_history, _subject).Handle(new ClearSearchHistoryCommand(), CancellationToken.None))
            .Data.ShouldBe(4);

        new PinSearchHistoryCommand(5, true).AuditSummary.ShouldBe("investigation:search-history:5:pin");
        new PinSearchHistoryCommand(5, false).AuditSummary.ShouldBe("investigation:search-history:5:unpin");
        new DeleteSearchHistoryCommand(7).AuditSummary.ShouldBe("investigation:search-history:7:delete");
        new ClearSearchHistoryCommand().AuditSummary.ShouldBe("investigation:search-history:clear");
        new DeleteSearchHistoryCommand(7).AuditAction.ShouldBe(AuditAction.Modify);

        // Без определённого сотрудника — пустая история и ни одного изменения.
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await new ListSearchHistoryQuery.Handler(_history, _subject, _accessProvider).Handle(new ListSearchHistoryQuery(), CancellationToken.None))
            .Data.ShouldBeEmpty();
        (await pin.Handle(new PinSearchHistoryCommand(5, true), CancellationToken.None)).StatusMessage.ShouldBe(SearchHistoryMessages.NotFound);
    }

    // ---------------- страница ----------------

    [Fact(DisplayName = "Состояние страницы в адресе: умолчания не пишутся; виды и отдел читаются обратно; мусор — умолчание")]
    public void Page_state_round_trips()
    {
        new SearchPageState().ToUri().ShouldBe("/search");
        var state = new SearchPageState("01 KG 123", SearchKinds.Vehicles | SearchKinds.Persons, CaseDirection.Technical);
        var uri = state.ToUri();
        uri.ShouldBe("/search?q=01%20KG%20123&kinds=Persons,Vehicles&direction=otm");
        SearchPageState.Parse(new Dictionary<string, string?>
        {
            ["q"] = "01 KG 123",
            ["kinds"] = "Persons,Vehicles",
            ["direction"] = "otm",
        }).ShouldBe(state);
        SearchPageState.Parse(new Dictionary<string, string?> { ["kinds"] = "All,Passwords,1", ["direction"] = "x" })
            .ShouldBe(new SearchPageState());
    }

    [Fact(DisplayName = "Выделение: без учёта регистра и «ё/е», все вхождения, текст строки не меняется (куски склеиваются в исходный)")]
    public void Highlight_is_case_and_yo_insensitive()
    {
        var parts = SearchHighlight.Split("Пётр Петров, ПЕТР", "петр");
        parts.Where(p => p.IsMatch).Select(p => p.Text).ShouldBe(["Пётр", "Петр", "ПЕТР"]);
        string.Concat(parts.Select(p => p.Text)).ShouldBe("Пётр Петров, ПЕТР");
        SearchHighlight.Split("Кража", "  ").ShouldBe([new SearchHighlightPart("Кража", false)]);
        SearchHighlight.Split(null, "x").ShouldBeEmpty();
    }

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<UnifiedSearchResult>> HandleAsync(UnifiedSearchQuery query) =>
        await new UnifiedSearchQuery.Handler(
                _cases, _persons, _requisites, _events, _reports, _caseDocuments, _documents, _media, _accessProvider, _subject, _history)
            .Handle(query, CancellationToken.None);

    private static CaseRow Case(int id, string number, string title, string? task = null) =>
        new(id, number, title, CaseKind.ObjectTask, CaseStatus.InProgress, new DateOnly(2026, 10, 1), 10, 5, 1, 0, 0, TaskNumber: task);

    private static PersonRow Person(int id) => new(id, 1, "Иванов " + id, false, null, null, null, 1, 5, 0, 0);
}

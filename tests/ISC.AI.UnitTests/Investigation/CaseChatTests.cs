using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.AI.Grounding;
using ISC.AI.Modules.Chat.Application;
using ISC.AI.Modules.Chat.Domain.Services;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.CaseChat;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Чат по материалам дел «Следствия» (ТФ-ДДЛ-02/03, ADR-0051/0052) — то, что остаётся профилю после выноса чата в пакет:
/// извлечение ссылок для ядрового валидатора, промпт-шаблон, тексты материалов в корпусе, сборка материалов области и
/// порт <see cref="IChatProfile"/> (области-дела, право матрицы, подписи источников, причины отказа, подсказки).
/// Сценарии вопроса, поиска и диалогов — общие, их проверяет <c>ChatModuleTests</c>.
/// </summary>
public sealed class CaseChatTests
{
    private const int CaseId = 3;
    private const int OtherCaseId = 4;
    private const int UserId = 42;

    private static readonly AccessContext Access = new(UserId.ToString(CultureInfo.InvariantCulture), 3, [5]);
    private static readonly CaseChatScope Case3 = new(CaseId, "12-2026/045", 2, 5, "Наблюдение за Ивановым");
    private static readonly CaseChatScope Case4 = new(OtherCaseId, "12-2026/046", 1, 5, "Другое дело");

    // ---------------- ссылки ----------------

    [Fact(DisplayName = "Ссылки: номера документов, статьи, части и кодексы извлекаются; «[Фрагмент N]» — нет; повторы отсечены")]
    public void Extractor_finds_anchored_references()
    {
        var citations = new CaseCitationExtractor().Extract(
            "По справке № 12-3/2026 [Фрагмент 1] деяние подпадает под ч. 2 ст. 158 Уголовного кодекса; см. также статью 158 и № 12-3/2026.");

        citations.ShouldBe(["№ 12-3/2026", "ч. 2", "ст. 158", "Уголовного кодекса", "статью 158"]);
        new CaseCitationExtractor().Extract("Ответа не найдено [Фрагмент 2].").ShouldBeEmpty();

        // Адреса и слова, внутри которых есть «част…», «ст.», «п.», — не ссылки (ADR-0053); формы «части 3», «частью 1» — ссылки.
        new CaseCitationExtractor().Extract("Земельный участок 12, ул. Частная 5, пост. 3, сп. 4.").ShouldBeEmpty();
        new CaseCitationExtractor().Extract("по части 3 и частью 1").ShouldBe(["части 3", "частью 1"]);
    }

    [Fact(DisplayName = "Проверка ядром: ссылка из поданного фрагмента подтверждена в любой форме записи, «по памяти» — не подтверждена")]
    public void Validator_confirms_only_supplied_references()
    {
        var validator = new GroundingValidator(new CaseCitationExtractor(), new CaseCitationNormalizer());
        var fragments = new List<RetrievedChunk> { new(1, 10, "Согласно справке №12-3/2026 изъятие проведено по статье 158 УК.", 1, 5, true, 0.1) };

        var result = validator.Validate("Изъятие по ст. 158 [Фрагмент 1], справка № 12-3/2026; ранее — № 99/2025, ст. 30.", fragments);

        result.AllConfirmed.ShouldBeFalse();
        result.Citations.Where(c => c.Status == CitationStatus.Confirmed).Select(c => c.Citation).ShouldBe(["ст. 158", "№ 12-3/2026"]);
        result.Citations.Where(c => c.Status == CitationStatus.Unverified).Select(c => c.Citation).ShouldBe(["№ 99/2025", "ст. 30"]);
    }

    [Fact(DisplayName = "Промпт — файл-шаблон: область подставлена; виды материалов, ссылки, «ответа не найдено», время записи и данные-не-указания на месте")]
    public void Prompt_template_is_rendered()
    {
        var prompt = CaseChatPrompt.Render("дела № 12-2026/045");

        prompt.ShouldContain("материалами дела № 12-2026/045");
        prompt.ShouldContain("расшифровки записей");
        prompt.ShouldContain("[Фрагмент 2]");
        prompt.ShouldContain("ответа не найдено");
        prompt.ShouldContain("время записи");
        prompt.ShouldContain("не выполняй");
    }

    // ---------------- тексты материалов ----------------

    [Fact(DisplayName = "Текст расшифровки: шапка с носителем, каждый фрагмент — абзац «[чч:мм:сс] текст»; момент первой метки разбирается")]
    public void Transcript_text_has_timecodes()
    {
        var text = TranscriptCorpusText.Build(7, "звонок.mp3", new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            [(5_000, " Алло, это я "), (65_500, "Встречаемся у рынка"), (70_000, "  ")]);

        text.ShouldBe("Расшифровка записи «звонок.mp3» (носитель № 7), запись от 12.10.2026. Автоматическая расшифровка речи — возможны ошибки распознавания."
            + "\n\n[00:00:05] Алло, это я\n\n[00:01:05] Встречаемся у рынка");
        TranscriptCorpusText.Timecode(3_725_000).ShouldBe("[01:02:05]");
        TranscriptCorpusText.FirstTimecodeMs("[00:01:05] Встречаемся у рынка\n[00:01:10] да").ShouldBe(65_000);
        TranscriptCorpusText.FirstTimecodeMs("без меток").ShouldBeNull();
    }

    [Fact(DisplayName = "Сведения дела: реквизиты и каждый фигурант абзацем — анкета, адреса, транспорт; сводка — события абзацами, справка — разделы")]
    public void Case_card_and_report_texts()
    {
        var card = CaseCorpusText.Card(
            new CaseCardHead(CaseId, "12-2026/045", "Наблюдение", CaseKind.CriminalCase, CaseStatus.InProgress, new DateOnly(2026, 9, 1), "З-17", null, "Установить связи"),
            [
                new CaseCardPerson("Иванов Иван Иванович", "Ваня", PersonRole.Target, null, PersonSex.Male, new DateOnly(1985, 3, 2), null,
                    "г. Ош", "рынок «Дордой»", "г. Бишкек, ул. Ленина, 15", null,
                    [(AddressKind.Stay, "г. Бишкек, ул. Токтогула, 1")], ["Toyota Camry белый, госномер 01KG123ABC"]),
            ]);

        card.ShouldStartWith("Сведения дела № 12-2026/045 «Наблюдение»");
        card.ShouldContain("№ задания: З-17.");
        card.ShouldContain("\n\nФигурант дела № 12-2026/045: Иванов Иван Иванович (псевдоним «Ваня»); роль — Объект; пол — мужской; дата рождения 02.03.1985");
        card.ShouldContain("адрес (пребывание) — г. Бишкек, ул. Токтогула, 1");
        card.ShouldContain("транспорт — Toyota Camry белый, госномер 01KG123ABC.");

        // ADR-0055: гражданство — после места рождения, телефоны — после транспорта, ориентировка — перед примечанием.
        var withPhones = CaseCorpusText.Card(
            new CaseCardHead(CaseId, "12-2026/045", "Наблюдение", CaseKind.CriminalCase, CaseStatus.InProgress, new DateOnly(2026, 9, 1), null, null, null),
            [
                new CaseCardPerson("Петров Пётр", null, PersonRole.Link, null, null, null, null, "г. Ош", null, null, "знакомый", [], [],
                    Citizenship: "Казахстан", Orientation: "ездит на такси", Phones: [(PhoneKind.Mobile, "+996 555 123 456")]),
            ]);
        withPhones.ShouldContain("место рождения — г. Ош; гражданство — Казахстан");
        withPhones.ShouldContain("телефон (мобильный) — +996 555 123 456; ориентировка — ездит на такси; примечание — знакомый.");

        var summary = CaseCorpusText.Report(55, "12-2026/045", CaseReportKind.SummaryOn, new DateOnly(2026, 10, 12), "Иванов И.И.", 2,
            new CaseReportContent(Events: [new SummaryEvent("14:30", "ул. Ленина, 15", "вышел из дома", null, "Toyota Camry 01KG123ABC")], Conclusion: "Наблюдение продолжить"));
        summary.ShouldStartWith("Сводка ОН от 12.10.2026 по делу № 12-2026/045 (документ № 55, редакция 2), объект: Иванов И.И..");
        summary.ShouldContain("\n\nСобытие: 14:30 — ул. Ленина, 15; вышел из дома; транспорт — Toyota Camry 01KG123ABC.");
        summary.ShouldContain("\n\nВывод: Наблюдение продолжить");

        var reference = CaseCorpusText.Report(56, "12-2026/045", CaseReportKind.ReferenceUn, new DateOnly(2026, 10, 13), null, 1,
            new CaseReportContent(Identity: "Иванов И.И., 1985 г.р.", Family: "Жена — Иванова А.А."));
        reference.ShouldContain("\n\nУстановочные данные: Иванов И.И., 1985 г.р.");
        reference.ShouldContain("\n\nСемейные и иные связи: Жена — Иванова А.А.");
        reference.ShouldNotContain("Событие");
    }

    // ---------------- область и материалы ----------------

    [Fact(DisplayName = "Область в терминах дел: одно дело (не видно — null) или все видимые; описание для промпта и наибольший гриф")]
    public async Task Area_resolution()
    {
        var scopes = Substitute.For<ICaseChatScopes>();
        scopes.GetScopeAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns(Case3);
        scopes.ListScopesAsync(Access, Arg.Any<CancellationToken>()).Returns([Case3, Case4]);

        var single = (await CaseChatArea.ResolveAsync(scopes, CaseId, Access, CancellationToken.None)).ShouldNotBeNull();
        single.Describe().ShouldBe("дела № 12-2026/045 «Наблюдение за Ивановым»");
        single.CaseIds.ShouldBe([CaseId]);
        var all = (await CaseChatArea.ResolveAsync(scopes, null, Access, CancellationToken.None)).ShouldNotBeNull();
        all.Describe().ShouldBe("всех доступных вам дел (2)");
        all.MaxClassification.ShouldBe((short)2);
        all.NumberOf(OtherCaseId).ShouldBe("12-2026/046");
        (await CaseChatArea.ResolveAsync(scopes, 99, Access, CancellationToken.None)).ShouldBeNull();
    }

    [Fact(DisplayName = "Материалы: видимые документы дел (в области — проиндексированные), расшифровки записей дел, сводки и сведения; готовность по видам")]
    public async Task Materials_are_collected_with_coverage()
    {
        var kit = new Kit();

        var materials = await kit.Resolver.ResolveAsync(new CaseChatArea([Case3, Case4], null), Access, sync: true, CancellationToken.None);

        materials.Items.ShouldBe(
        [
            new ChatMaterial(501, ChatSourceKind.Document, 77, CaseId, "№ 12-3/2026 от 01.10.2026 · Справка · дело № 12-2026/045"),
            new ChatMaterial(601, ChatSourceKind.Transcript, 9, OtherCaseId, "Расшифровка «звонок.mp3» · носитель № 9 · дело № 12-2026/046"),
            new ChatMaterial(701, ChatSourceKind.Report, 55, CaseId, "Сводка ОН от 12.10.2026 · дело № 12-2026/045"),
            new ChatMaterial(801, ChatSourceKind.CaseCard, CaseId, CaseId, "Сведения дела № 12-2026/045"),
        ]);
        materials.Coverage.ShouldBe(new ChatCoverage(2, DocumentsVisible: 2, DocumentsIndexed: 1, TranscriptsReady: 2, TranscriptsIndexed: 1, Reports: 1, CaseCards: 1));
        materials.CoreDocumentIds.ShouldBe([501, 601, 701, 801]);
        await kit.Corpus.Received(1).SyncAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { CaseId, OtherCaseId })), Arg.Any<CancellationToken>());

        await kit.Resolver.ResolveAsync(new CaseChatArea([Case3], Case3), Access, sync: false, CancellationToken.None);
        await kit.Corpus.Received(1).ListAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
        (await kit.Resolver.ResolveAsync(new CaseChatArea([], null), Access, sync: true, CancellationToken.None)).ShouldBe(ChatMaterials.Empty);
    }

    // ---------------- порт профиля для пакета «Чат» (ADR-0052) ----------------

    [Fact(DisplayName = "Настройки «Следствия»: без свободного режима, с поиском по смыслу; режим по материалам — «Спросить»")]
    public void Settings_of_investigation()
    {
        var settings = new Kit().Profile.Settings;

        settings.AllowFreeMode.ShouldBeFalse();
        settings.AllowSenseSearch.ShouldBeTrue();
        settings.GroundedModeLabel.ShouldBe("Спросить");
        settings.TopK.ShouldBe(10);
        settings.HistoryTurns.ShouldBe(6);
        settings.HistoryTurnLength.ShouldBe(1_500);
        settings.SenseSearchTopK.ShouldBe(20);
    }

    [Theory(DisplayName = "Право на чат — «Чат по материалам дел» матрицы доступа")]
    [InlineData(InvestigationRole.Investigator, true)]
    [InlineData(InvestigationRole.Head, true)]
    [InlineData(InvestigationRole.Verifier, false)]
    public async Task Right_comes_from_access_matrix(InvestigationRole role, bool allowed)
    {
        var kit = new Kit();
        kit.Roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(role);

        (await kit.Profile.CanUseAsync()).ShouldBe(allowed);
    }

    [Fact(DisplayName = "Области: «Все мои дела» первой (без грифа вопросов, журнал — с наибольшим грифом), затем каждое дело со своим грифом, подразделением, ключом уничтожения и ссылкой на карточку")]
    public async Task Areas_are_cases()
    {
        var areas = await new Kit().Profile.ListAreasAsync(Access);

        areas.Select(a => a.Key).ShouldBe([null, "case:3", "case:4"]);
        var all = areas[0];
        all.Title.ShouldBe("Все мои дела (2)");
        all.Description.ShouldBe("всех доступных вам дел (2)");
        (all.Classification, all.AuditClassification, all.DivisionId, all.AuditRef).ShouldBe(((short)0, (short)2, (int?)null, "investigation"));
        all.ScopeKeys.ShouldBeEmpty();
        all.Link.ShouldBeNull();

        var one = areas[1];
        one.Title.ShouldBe("№ 12-2026/045 — Наблюдение за Ивановым");
        one.Description.ShouldBe("дела № 12-2026/045 «Наблюдение за Ивановым»");
        (one.Classification, one.AuditClassification, one.DivisionId, one.AuditRef).ShouldBe(((short)2, (short)2, (int?)5, "investigation:case:3"));
        one.ScopeKeys.ShouldBe(["case:3"]);
        one.Link.ShouldBe(new ChatLink("/cases/3", "Карточка дела"));
        areas[2].Title.ShouldBe("№ 12-2026/046 — Другое дело");
    }

    [Theory(DisplayName = "Область по ключу: без ключа — все дела, «case:N» — видимое дело; чужое, невидимое и непонятное — null")]
    [InlineData(null, null, true)]
    [InlineData("  ", null, true)]
    [InlineData("case:3", "case:3", true)]
    [InlineData(" case:3 ", "case:3", true)]
    [InlineData("case:99", null, false)]
    [InlineData("case:0", null, false)]
    [InlineData("case:-3", null, false)]
    [InlineData("case:abc", null, false)]
    [InlineData("cases:3", null, false)]
    [InlineData("CASE:3", null, false)]
    public async Task Area_by_key(string? key, string? expectedKey, bool found)
    {
        var area = await new Kit().Profile.ResolveAreaAsync(key, Access);

        if (found)
        {
            area.ShouldNotBeNull().Key.ShouldBe(expectedKey);
        }
        else
        {
            area.ShouldBeNull();
        }
    }

    [Fact(DisplayName = "Материалы области: документы корпуса, строка «что видит чат»; подписи — документ, расшифровка на моменте записи, сводка, сведения; ключ уничтожения — дело материала")]
    public async Task Scope_describes_sources()
    {
        var kit = new Kit();
        var all = (await kit.Profile.ResolveAreaAsync(null, Access)).ShouldNotBeNull();

        var scope = await kit.Profile.ResolveScopeAsync(all, Access, forQuestion: true);

        scope.DocumentIds.ShouldBe([501, 601, 701, 801]);
        scope.Unavailable.ShouldBeNull();
        scope.Coverage.ShouldBe("Дел: 2 · Чат видит: документов 1 из 2 · расшифровок 1 из 2 · сводок и справок 1 · сведений дел 1");
        scope.CoverageHint.ShouldNotBeNullOrWhiteSpace();
        await kit.Corpus.Received(1).SyncAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());

        scope.Describe(Chunk(501, "Изъяты телефон")).ShouldBe(new ChatSourceRef(
            "документ", "№ 12-3/2026 от 01.10.2026 · Справка · дело № 12-2026/045", "/docflow/documents/77", "case:3"));
        scope.Describe(Chunk(601, "[00:01:05] Встречаемся у рынка")).ShouldBe(new ChatSourceRef(
            "расшифровка", "Расшифровка «звонок.mp3» · носитель № 9 · дело № 12-2026/046 · [00:01:05]", "/media/assets/9?t=65000", "case:4"));
        scope.Describe(Chunk(601, "без меток времени")).ShouldNotBeNull().Href.ShouldBe("/media/assets/9");
        scope.Describe(Chunk(701, "Событие")).ShouldBe(new ChatSourceRef("сводка/справка", "Сводка ОН от 12.10.2026 · дело № 12-2026/045", "/reports/55", "case:3"));
        scope.Describe(Chunk(801, "Сведения")).ShouldBe(new ChatSourceRef("сведения дела", "Сведения дела № 12-2026/045", "/cases/3", "case:3"));
        scope.Describe(Chunk(999, "чужой")).ShouldBeNull();

        // Строка готовности — без подготовки материалов (без синхронизации с корпусом).
        await kit.Profile.ResolveScopeAsync(all, Access, forQuestion: false);
        await kit.Corpus.Received(1).ListAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Спрашивать нельзя: нет дел, нечего читать, дело стало недоступно — причина профиля; узел ИИ при синхронизации — исключение для пакета")]
    public async Task Scope_unavailable_reasons()
    {
        var kit = new Kit();
        var one = (await kit.Profile.ResolveAreaAsync("case:3", Access)).ShouldNotBeNull();

        kit.Corpus.SyncAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Throws(new ModelUnavailableException(ModelRole.Embeddings, null));
        await Should.ThrowAsync<ModelUnavailableException>(() => kit.Profile.ResolveScopeAsync(one, Access, forQuestion: true));

        kit.Corpus.SyncAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([]);
        kit.Documents.ResolveCorpusAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>()).Returns([]);
        kit.Transcripts.ResolveAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>()).Returns(new TranscriptCorpusScope([], 0));
        var empty = await kit.Profile.ResolveScopeAsync(one, Access, forQuestion: true);
        empty.Unavailable.ShouldBe(CaseChatTexts.NoMaterials);
        empty.DocumentIds.ShouldBeEmpty();

        kit.Scopes.ListScopesAsync(Access, Arg.Any<CancellationToken>()).Returns([]);
        var all = (await kit.Profile.ResolveAreaAsync(null, Access)).ShouldNotBeNull();
        all.Title.ShouldBe("Все мои дела (0)");
        (await kit.Profile.ResolveScopeAsync(all, Access, forQuestion: true)).Unavailable.ShouldBe(CaseChatTexts.NoCases);

        kit.Scopes.GetScopeAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns((CaseChatScope?)null);
        (await kit.Profile.ResolveScopeAsync(one, Access, forQuestion: true)).Unavailable.ShouldBe(CaseChatTexts.CaseGone);
    }

    [Fact(DisplayName = "Промпт области — файл-шаблон с описанием области; подсказки — свои для одного дела и для всех дел, плюс поиск по смыслу")]
    public async Task Prompt_and_suggestions()
    {
        var kit = new Kit();
        var one = (await kit.Profile.ResolveAreaAsync("case:3", Access)).ShouldNotBeNull();
        var all = (await kit.Profile.ResolveAreaAsync(null, Access)).ShouldNotBeNull();

        kit.Profile.TaskPrompt(one).ShouldNotBeNull().ShouldContain("материалами дела № 12-2026/045 «Наблюдение за Ивановым»");
        kit.Profile.TaskPrompt(all).ShouldNotBeNull().ShouldContain("всех доступных вам дел (2)");
        kit.Profile.Suggestions(one).Grounded.ShouldBe(CaseChatSuggestions.OneCase);
        kit.Profile.Suggestions(all).Grounded.ShouldBe(CaseChatSuggestions.AllCases);
        kit.Profile.Suggestions(all).Sense.ShouldBe(CaseChatSuggestions.Sense);
        kit.Profile.Suggestions(all).Free.ShouldBeEmpty();
        foreach (var list in new[] { CaseChatSuggestions.AllCases, CaseChatSuggestions.OneCase, CaseChatSuggestions.Sense })
        {
            list.Count.ShouldBeGreaterThanOrEqualTo(3);
            list.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s) && s.Length <= ChatLimits.MaxQuestionLength);
            list.ShouldBeUnique();
        }
    }

    [Theory(DisplayName = "Ключ области дела: «case:N» туда и обратно; иное — не дело")]
    [InlineData("case:3", true, 3)]
    [InlineData("case:12345", true, 12345)]
    [InlineData("case:", false, 0)]
    [InlineData("case: 3", false, 0)]
    [InlineData("case:3x", false, 0)]
    [InlineData(null, false, 0)]
    public void Area_key_round_trip(string? key, bool parsed, int caseId)
    {
        CaseChatAreas.TryParse(key, out var id).ShouldBe(parsed);
        id.ShouldBe(caseId);
        if (parsed)
        {
            CaseChatAreas.KeyOf(id).ShouldBe(key);
        }
    }

    // ---------------- обвязка ----------------

    private static RetrievedChunk Chunk(int documentId, string text) => new(900 + documentId, documentId, text, 1, 5, true, 0.2);

    private sealed class Kit
    {
        public Kit()
        {
            Subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)UserId);
            Roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
            Scopes.GetScopeAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns(Case3);
            Scopes.GetScopeAsync(OtherCaseId, Access, Arg.Any<CancellationToken>()).Returns(Case4);
            Scopes.ListScopesAsync(Access, Arg.Any<CancellationToken>()).Returns([Case3, Case4]);

            CaseDocuments.ListLinksAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
                .Returns([new CaseDocumentRef(CaseId, 77), new CaseDocumentRef(CaseId, 78), new CaseDocumentRef(OtherCaseId, 77)]);
            Documents.ResolveByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>()).Returns(new Dictionary<int, DocumentBrief>
            {
                [77] = new(77, "12-3/2026", new DateOnly(2026, 10, 1), "Справка", "об изъятии", UserId, default(DocumentAggregatedStatus), 1),
                [78] = new(78, null, new DateOnly(2026, 10, 2), "Рапорт", "без текста", UserId, default(DocumentAggregatedStatus), 1),
            });
            Documents.ResolveCorpusAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
                .Returns([new DocumentCorpusRef(77, 501, "12-3/2026", new DateOnly(2026, 10, 1), "Справка")]);

            Cases.ListMediaAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([9, 10]);
            // Запись 9 привязана к делу 4; в области только дела 3 — как в настоящем хранилище, берётся привязка из области.
            Cases.MapMediaAssetsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                .Returns(ci => (IReadOnlyDictionary<int, int>)new Dictionary<int, int>
                {
                    [9] = ci.ArgAt<IReadOnlyCollection<int>>(1).Contains(OtherCaseId) ? OtherCaseId : CaseId,
                });
            Transcripts.ResolveAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
                .Returns(new TranscriptCorpusScope([new TranscriptCorpusRef(9, 601, "звонок.mp3", MediaKind.Audio, 1)], 2));

            IReadOnlyList<CaseCorpusRef> profile =
            [
                new(CaseCorpusKind.Report, 55, CaseId, 701, "Сводка ОН от 12.10.2026 · дело № 12-2026/045"),
                new(CaseCorpusKind.CaseCard, CaseId, CaseId, 801, "Сведения дела № 12-2026/045"),
            ];
            Corpus.SyncAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(profile);
            Corpus.ListAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns(profile);
            Resolver = new ChatMaterialResolver(CaseDocuments, Documents, Cases, Transcripts, Corpus);
            Profile = new InvestigationChatProfile(Scopes, Resolver, Roles, Subject);
        }

        public ICaseChatScopes Scopes { get; } = Substitute.For<ICaseChatScopes>();

        public ICaseDocumentStore CaseDocuments { get; } = Substitute.For<ICaseDocumentStore>();

        public IDocumentLookup Documents { get; } = Substitute.For<IDocumentLookup>();

        public ICaseStore Cases { get; } = Substitute.For<ICaseStore>();

        public ITranscriptCorpus Transcripts { get; } = Substitute.For<ITranscriptCorpus>();

        public ICaseCorpusSync Corpus { get; } = Substitute.For<ICaseCorpusSync>();

        public ChatMaterialResolver Resolver { get; }

        public InvestigationChatProfile Profile { get; }

        public IUserRoleStore Roles { get; } = Substitute.For<IUserRoleStore>();

        public ISubjectProvider Subject { get; } = Substitute.For<ISubjectProvider>();
    }
}

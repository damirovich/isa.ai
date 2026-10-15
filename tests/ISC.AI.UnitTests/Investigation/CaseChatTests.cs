using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.AI.Grounding;
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
/// Чат и поиск по смыслу по материалам дел (ТФ-ДДЛ-02/03, ADR-0051): ссылки ответа проверяются по поданным фрагментам;
/// область — одно дело или все доступные; материалы — документы дел, расшифровки записей (с моментом записи), сводки и
/// сведения дел; журнал — до модели; без материалов, права или доступа модель не вызывается; источники сохраняются.
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

    [Fact(DisplayName = "«[Фрагмент N]»: номера по порядку без повторов; вне поданных — неизвестны; разбиение сохраняет текст как написан")]
    public void Fragment_references_are_found_checked_and_split()
    {
        const string answer = "Изъятие [Фрагмент 2], протокол во фрагменте 1, и ещё [фрагмент №7]; снова [Фрагмент 2].";

        FragmentReferences.Find(answer).ShouldBe([2, 1, 7]);
        FragmentReferences.Unknown(answer, supplied: 3).ShouldBe([7]);
        var parts = FragmentReferences.Split(answer);
        string.Concat(parts.Select(p => p.Text)).ShouldBe(answer);
        parts.Where(p => p.Fragment is not null).Select(p => (p.Text, p.Fragment)).ShouldBe(
        [
            ("[Фрагмент 2]", (int?)2), ("фрагменте 1", 1), ("[фрагмент №7]", 7), ("[Фрагмент 2]", 2),
        ]);
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

    [Fact(DisplayName = "Оформление ответа: заголовок «**…**» и «#», пункты «* » и «- », жирный внутри строки и поверх ссылки; непарная «**» — текстом")]
    public void Answer_markup_is_parsed()
    {
        var blocks = AnswerMarkup.Parse(
            "**Дело № Б-2/26 «Сбыт»:**\n\n*   Петров — **Toyota Camry** [Фрагмент 1].\n- **Иванов [Фрагмент 2]**\n### Итог\nВсего 2 ** фигуранта");

        blocks.Select(b => b.Kind).ShouldBe(
            [AnswerBlockKind.Heading, AnswerBlockKind.Bullet, AnswerBlockKind.Bullet, AnswerBlockKind.Heading, AnswerBlockKind.Paragraph]);
        blocks[0].Spans.ShouldBe([new AnswerSpan("Дело № Б-2/26 «Сбыт»:", true, null)]);
        blocks[1].Spans.ShouldBe(
        [
            new AnswerSpan("Петров — ", false, null), new AnswerSpan("Toyota Camry", true, null), new AnswerSpan(" ", false, null),
            new AnswerSpan("[Фрагмент 1]", false, 1), new AnswerSpan(".", false, null),
        ]);
        blocks[2].Spans.ShouldBe([new AnswerSpan("Иванов ", true, null), new AnswerSpan("[Фрагмент 2]", true, 2)]);
        blocks[3].Spans.ShouldBe([new AnswerSpan("Итог", true, null)]);
        string.Concat(blocks[4].Spans.Select(s => s.Text)).ShouldBe("Всего 2 ** фигуранта");
        blocks[4].Spans.ShouldAllBe(s => !s.Bold);
        AnswerMarkup.Parse("  \n ").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Подсказки: свои для всех дел, для одного дела и для поиска по смыслу; не пустые и без повторов")]
    public void Suggestions_are_defined()
    {
        ChatSuggestions.For(null).ShouldBe(ChatSuggestions.AllCases);
        ChatSuggestions.For(CaseId).ShouldBe(ChatSuggestions.OneCase);
        foreach (var list in new[] { ChatSuggestions.AllCases, ChatSuggestions.OneCase, ChatSuggestions.Sense })
        {
            list.Count.ShouldBeGreaterThanOrEqualTo(3);
            list.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s) && s.Length <= CaseChatLimits.MaxQuestionLength);
            list.ShouldBeUnique();
        }
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

    [Theory(DisplayName = "Источник открывается на месте: документ, сводка, дело или носитель на моменте записи")]
    [InlineData(ChatSourceKind.Document, 77, null, "/docflow/documents/77")]
    [InlineData(ChatSourceKind.Report, 55, null, "/reports/55")]
    [InlineData(ChatSourceKind.CaseCard, 3, null, "/cases/3")]
    [InlineData(ChatSourceKind.Transcript, 9, 65_000L, "/media/assets/9?t=65000")]
    [InlineData(ChatSourceKind.Transcript, 9, null, "/media/assets/9")]
    public void Source_href(ChatSourceKind kind, int target, long? startMs, string href) =>
        new CaseChatSource(1, kind, target, CaseId, "x", 1, 0, "x", startMs).Href.ShouldBe(href);

    // ---------------- область и материалы ----------------

    [Fact(DisplayName = "Область: одно дело (не видно — null) или все видимые; описание для промпта и наибольший гриф")]
    public async Task Area_resolution()
    {
        var chats = Substitute.For<ICaseChatStore>();
        chats.GetScopeAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns(Case3);
        chats.ListScopesAsync(Access, Arg.Any<CancellationToken>()).Returns([Case3, Case4]);

        var single = (await ChatArea.ResolveAsync(chats, CaseId, Access, CancellationToken.None)).ShouldNotBeNull();
        single.Describe().ShouldBe("дела № 12-2026/045 «Наблюдение за Ивановым»");
        single.CaseIds.ShouldBe([CaseId]);
        var all = (await ChatArea.ResolveAsync(chats, null, Access, CancellationToken.None)).ShouldNotBeNull();
        all.Describe().ShouldBe("всех доступных вам дел (2)");
        all.MaxClassification.ShouldBe((short)2);
        all.NumberOf(OtherCaseId).ShouldBe("12-2026/046");
        (await ChatArea.ResolveAsync(chats, 99, Access, CancellationToken.None)).ShouldBeNull();
    }

    [Fact(DisplayName = "Материалы: видимые документы дел (в области — проиндексированные), расшифровки записей дел, сводки и сведения; готовность по видам")]
    public async Task Materials_are_collected_with_coverage()
    {
        var kit = new Kit();

        var materials = await kit.Resolver.ResolveAsync(new ChatArea([Case3, Case4], null), Access, sync: true, CancellationToken.None);

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

        await kit.Resolver.ResolveAsync(new ChatArea([Case3], Case3), Access, sync: false, CancellationToken.None);
        await kit.Corpus.Received(1).ListAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>());
        (await kit.Resolver.ResolveAsync(new ChatArea([], null), Access, sync: true, CancellationToken.None)).ShouldBe(ChatMaterials.Empty);
    }

    // ---------------- вопрос ----------------

    [Fact(DisplayName = "Вопрос по делу: новый диалог, журнал — до модели, модель ищет только в материалах области; источники с моментом записи и проверка")]
    public async Task Question_on_case_goes_through_scope_audit_and_grounding()
    {
        var kit = new Kit();
        var order = new List<string>();
        kit.Audit.When(a => a.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())).Do(_ => order.Add("audit"));
        GroundedRequest? sent = null;
        kit.Generator.GenerateStreamingAsync(Arg.Do<GroundedRequest>(r => { sent = r; order.Add("model"); }), Access, Arg.Any<CancellationToken>())
            .Returns(Stream(
                new GroundedStreamUpdate("Изъяли ", null),
                new GroundedStreamUpdate(null, Final("Изъяли телефон [Фрагмент 1]; в записи [Фрагмент 2] — встреча; см. [Фрагмент 5] и № 99/2025."))));

        var updates = await kit.SendAsync(new SendCaseChatMessageCommand(CaseId, null, "  Что изъяли?  "));

        order.ShouldBe(["audit", "model"]);
        updates[0].ChatId.ShouldBe(15);
        updates[1].TextDelta.ShouldBe("Изъяли ");
        var final = updates[^1].Final.ShouldNotBeNull();

        await kit.Chats.Received(1).CreateAsync(CaseId, "Что изъяли?", Access, Arg.Any<CancellationToken>());
        await kit.Chats.Received(1).AppendAsync(15, Arg.Is<CaseChatMessageDraft>(m =>
            m.Role == ConversationMessageRole.User && m.Text == "Что изъяли?" && m.Classification == 2), Access, Arg.Any<CancellationToken>());
        await kit.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e =>
            e.Action == AuditAction.Search && e.ObjectRef == "investigation:case:3:chat:15" && e.Classification == 2 && e.DivisionId == 5
            && e.SubjectId == UserId && e.PayloadSensitive!.Contains("Что изъяли?", StringComparison.Ordinal)
            && e.PayloadSensitive.Contains("дела № 12-2026/045", StringComparison.Ordinal)), Arg.Any<CancellationToken>());

        sent.ShouldNotBeNull();
        sent.Filter.ShouldNotBeNull().DocumentIds.ShouldBe([501, 601, 701, 801]);
        sent.Role.ShouldBe(ModelRole.Draft);
        sent.TopK.ShouldBe(CaseChatMessages.TopK);
        sent.TaskPrompt.ShouldNotBeNull().ShouldContain("материалами дела № 12-2026/045");

        final.Sources.ShouldBe(
        [
            new CaseChatSource(1, ChatSourceKind.Document, 77, CaseId, "№ 12-3/2026 от 01.10.2026 · Справка · дело № 12-2026/045", 900, 1, "Изъяты телефон и записная книжка по ст. 158."),
            new CaseChatSource(2, ChatSourceKind.Transcript, 9, CaseId, "Расшифровка «звонок.mp3» · носитель № 9 · дело № 12-2026/045", 901, 1,
                "[00:01:05] Встречаемся у рынка", 65_000),
        ]);
        final.Check.Unverified.ShouldBe(["№ 99/2025"]);
        final.Check.UnknownFragments.ShouldBe([5]);
        final.Classification.ShouldBe((short)2);
        await kit.Chats.Received(1).AppendAsync(15, Arg.Is<CaseChatMessageDraft>(m =>
            m.Role == ConversationMessageRole.Assistant && m.Sources!.Count == 2 && m.Check!.UnknownFragments.Count == 1), Access, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Вопрос по всем делам: диалог без дела, журнал на диалог с наибольшим грифом области, вопрос без грифа, промпт — «всех доступных дел»")]
    public async Task Question_on_all_cases()
    {
        var kit = new Kit();
        GroundedRequest? sent = null;
        kit.Generator.GenerateStreamingAsync(Arg.Do<GroundedRequest>(r => sent = r), Access, Arg.Any<CancellationToken>())
            .Returns(Stream(new GroundedStreamUpdate(null, Final("Ответ [Фрагмент 1]."))));

        var updates = await kit.SendAsync(new SendCaseChatMessageCommand(null, null, "Кто ездит на Camry?"));

        updates[^1].Final.ShouldNotBeNull().Classification.ShouldBe((short)1);
        await kit.Chats.Received(1).CreateAsync(null, "Кто ездит на Camry?", Access, Arg.Any<CancellationToken>());
        await kit.Chats.Received(1).AppendAsync(15, Arg.Is<CaseChatMessageDraft>(m =>
            m.Role == ConversationMessageRole.User && m.Classification == 0), Access, Arg.Any<CancellationToken>());
        await kit.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e =>
            e.ObjectRef == "investigation:chat:15" && e.Classification == 2 && e.DivisionId == null
            && e.PayloadSensitive!.Contains("всех доступных вам дел (2)", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
        sent.ShouldNotBeNull().TaskPrompt.ShouldNotBeNull().ShouldContain("всех доступных вам дел (2)");
    }

    [Fact(DisplayName = "Вопрос в прежний диалог: история — последние реплики; диалог другой области — «не найдено», модель не вызывается")]
    public async Task Existing_chat_passes_history_and_rejects_other_area()
    {
        var kit = new Kit();
        var messages = Enumerable.Range(1, 8)
            .Select(i => new CaseChatMessageRow(i, i % 2 == 1 ? ConversationMessageRole.User : ConversationMessageRole.Assistant, "реплика " + i, 2, DateTime.UtcNow, [], null))
            .ToList();
        kit.Chats.GetAsync(15, Access, Arg.Any<CancellationToken>()).Returns(new CaseChatThread(15, CaseId, "Т", 2, messages, 0));
        GroundedRequest? sent = null;
        kit.Generator.GenerateStreamingAsync(Arg.Do<GroundedRequest>(r => sent = r), Access, Arg.Any<CancellationToken>())
            .Returns(Stream(new GroundedStreamUpdate(null, Final("Ответ [Фрагмент 1]."))));

        await kit.SendAsync(new SendCaseChatMessageCommand(CaseId, 15, "Ещё вопрос"));

        sent.ShouldNotBeNull().History.ShouldNotBeNull().Select(t => t.Text).ShouldBe(messages.Skip(2).Select(m => m.Text));
        await kit.Chats.DidNotReceiveWithAnyArgs().CreateAsync(default, default!, default!, default);

        kit.Generator.ClearReceivedCalls();
        (await kit.SendAsync(new SendCaseChatMessageCommand(null, 15, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(CaseChatMessages.NotFound);
        kit.Generator.DidNotReceiveWithAnyArgs().GenerateStreamingAsync(default!, default!, default);
    }

    [Fact(DisplayName = "Отказы до модели: пустой вопрос, нет права, дело не видно, нет дел, нет материалов, узел ИИ недоступен при синхронизации")]
    public async Task Refusals_before_model()
    {
        var kit = new Kit();
        (await kit.SendAsync(new SendCaseChatMessageCommand(CaseId, null, "   "))).ShouldHaveSingleItem().Error.ShouldBe(CaseChatMessages.EmptyQuestion);
        (await kit.SendAsync(new SendCaseChatMessageCommand(99, null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(CaseChatMessages.NotFound);

        kit.Corpus.SyncAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Throws(new ModelUnavailableException(ModelRole.Embeddings, null));
        (await kit.SendAsync(new SendCaseChatMessageCommand(CaseId, null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(CaseChatMessages.ModelUnavailable);

        kit.Corpus.SyncAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([]);
        kit.Documents.ResolveCorpusAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>()).Returns([]);
        kit.Transcripts.ResolveAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>()).Returns(new TranscriptCorpusScope([], 0));
        (await kit.SendAsync(new SendCaseChatMessageCommand(CaseId, null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(CaseChatMessages.NoMaterials);
        await kit.Chats.DidNotReceiveWithAnyArgs().CreateAsync(default, default!, default!, default);

        kit.Chats.ListScopesAsync(Access, Arg.Any<CancellationToken>()).Returns([]);
        (await kit.SendAsync(new SendCaseChatMessageCommand(null, null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(CaseChatMessages.NoCases);

        kit.Roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Verifier);
        (await kit.SendAsync(new SendCaseChatMessageCommand(CaseId, null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(CaseChatMessages.NoRight);

        kit.Generator.DidNotReceiveWithAnyArgs().GenerateStreamingAsync(default!, default!, default);
        await kit.Audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Fact(DisplayName = "Узел ИИ недоступен при ответе: понятная ошибка с номером диалога, вопрос уже сохранён, ответа нет")]
    public async Task Model_unavailable_is_reported()
    {
        var kit = new Kit();
        kit.Generator.GenerateStreamingAsync(Arg.Any<GroundedRequest>(), Access, Arg.Any<CancellationToken>())
            .Returns(Failing(new ModelUnavailableException(ModelRole.Draft, null)));

        var updates = await kit.SendAsync(new SendCaseChatMessageCommand(CaseId, null, "Вопрос"));

        updates[^1].Error.ShouldNotBeNull().ShouldStartWith(CaseChatMessages.ModelUnavailable);
        updates[^1].ChatId.ShouldBe(15);
        await kit.Chats.Received(1).AppendAsync(15, Arg.Is<CaseChatMessageDraft>(m => m.Role == ConversationMessageRole.User), Access, Arg.Any<CancellationToken>());
        await kit.Chats.DidNotReceive().AppendAsync(15, Arg.Is<CaseChatMessageDraft>(m => m.Role == ConversationMessageRole.Assistant), Access, Arg.Any<CancellationToken>());
    }

    // ---------------- поиск по смыслу ----------------

    [Fact(DisplayName = "Поиск по смыслу: в материалах области, близость = 1 − расстояние, источник с моментом записи; журнал — с текстом запроса")]
    public async Task Sense_search_scopes_to_materials()
    {
        var kit = new Kit();
        RetrievalFilter? filter = null;
        kit.Retriever.RetrieveAsync("встреча на рынке", Access, CaseChatMessages.SenseSearchTopK, Arg.Do<RetrievalFilter?>(f => filter = f), Arg.Any<CancellationToken>())
            .Returns([new RetrievedChunk(901, 601, "[00:01:05] Встречаемся у рынка", 1, 5, true, 0.25)]);

        var response = await kit.SearchAsync(new SearchCaseDocumentsQuery(null, "  встреча на рынке "));

        filter.ShouldNotBeNull().DocumentIds.ShouldBe([501, 601, 701, 801]);
        var hit = response.Data.ShouldNotBeNull().Hits.ShouldHaveSingleItem();
        hit.Similarity.ShouldBe(0.75);
        hit.Source.Href.ShouldBe("/media/assets/9?t=65000");
        hit.Source.Label.ShouldBe("Расшифровка «звонок.mp3» · носитель № 9 · дело № 12-2026/046");
        new SearchCaseDocumentsQuery(CaseId, " изъятие ").AuditSummary.ShouldBe("investigation:case:3:materials:sense-search;text=изъятие");
        new SearchCaseDocumentsQuery(null, "x").AuditSummary.ShouldBe("investigation:materials:sense-search;text=x");
        new SearchCaseDocumentsQuery(null, "x").AuditAction.ShouldBe(AuditAction.Search);
    }

    // ---------------- обвязка ----------------

    private static GroundedResponse Final(string answer) => new(
        answer,
        new GroundingValidator(new CaseCitationExtractor(), new CaseCitationNormalizer()).Validate(answer, Fragments),
        Fragments,
        1);

    private static readonly List<RetrievedChunk> Fragments =
    [
        new(900, 501, "Изъяты телефон и записная книжка по ст. 158.", 1, 5, true, 0.2),
        new(901, 601, "[00:01:05] Встречаемся у рынка", 1, 5, true, 0.3),
    ];

    private static async IAsyncEnumerable<GroundedStreamUpdate> Stream(params GroundedStreamUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

#pragma warning disable CS1998 // асинхронный итератор, который сразу бросает: так ведёт себя генератор при недоступной модели
    private static async IAsyncEnumerable<GroundedStreamUpdate> Failing(Exception exception, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998

    private sealed class Kit
    {
        public Kit()
        {
            Subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)UserId);
            Roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
            AccessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
            Chats.GetScopeAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns(Case3);
            Chats.ListScopesAsync(Access, Arg.Any<CancellationToken>()).Returns([Case3, Case4]);
            Chats.CreateAsync(Arg.Any<int?>(), Arg.Any<string>(), Access, Arg.Any<CancellationToken>()).Returns((int?)15);
            Chats.AppendAsync(Arg.Any<int>(), Arg.Any<CaseChatMessageDraft>(), Access, Arg.Any<CancellationToken>()).Returns(true);

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
        }

        public ICaseChatStore Chats { get; } = Substitute.For<ICaseChatStore>();

        public ICaseDocumentStore CaseDocuments { get; } = Substitute.For<ICaseDocumentStore>();

        public IDocumentLookup Documents { get; } = Substitute.For<IDocumentLookup>();

        public ICaseStore Cases { get; } = Substitute.For<ICaseStore>();

        public ITranscriptCorpus Transcripts { get; } = Substitute.For<ITranscriptCorpus>();

        public ICaseCorpusSync Corpus { get; } = Substitute.For<ICaseCorpusSync>();

        public ChatMaterialResolver Resolver { get; }

        public IGroundedGenerator Generator { get; } = Substitute.For<IGroundedGenerator>();

        public IRetriever Retriever { get; } = Substitute.For<IRetriever>();

        public IAuditWriter Audit { get; } = Substitute.For<IAuditWriter>();

        public IUserRoleStore Roles { get; } = Substitute.For<IUserRoleStore>();

        public ISubjectProvider Subject { get; } = Substitute.For<ISubjectProvider>();

        public IAccessContextProvider AccessProvider { get; } = Substitute.For<IAccessContextProvider>();

        public async Task<List<CaseChatUpdate>> SendAsync(SendCaseChatMessageCommand command)
        {
            var handler = new SendCaseChatMessageCommand.Handler(Chats, Resolver, Generator, Audit, Roles, Subject, AccessProvider);
            var updates = new List<CaseChatUpdate>();
            await foreach (var update in handler.Handle(command, CancellationToken.None))
            {
                updates.Add(update);
            }

            return updates;
        }

        public async Task<ISC.AI.Abstractions.Application.ResponseDto<CaseSenseSearchResult>> SearchAsync(SearchCaseDocumentsQuery query) =>
            await new SearchCaseDocumentsQuery.Handler(Chats, Resolver, Retriever, Roles, Subject, AccessProvider)
                .Handle(query, CancellationToken.None);
    }
}

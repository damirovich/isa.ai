using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat;
using ISC.AI.Modules.Chat.Domain.Services;
using ISC.AI.Profile.Inspector;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Chat;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Application.Features.Guide;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// «Чат-ассистент» «ИнспектораAI» на пакете «Чат» (ADR-0052): область по умолчанию — весь корпус по допуску, свободный
/// режим по умолчанию и «По документам» без задачного промпта (как до пакета), без поиска по смыслу; источник — карточка
/// документа документооборота или карточка документа корпуса; страница — из пакета, в «ИИ-инструментах». Вторая
/// область — помощник «Как пользоваться системой» по справке пользователя (ADR-0067).
/// </summary>
public sealed class InspectorChatProfileTests
{
    private static readonly AccessContext Access = new("7", 2, [1]);
    private readonly InspectorChatProfile _profile =
        new(new EmbeddedScribanPromptProvider(typeof(InspectorApplicationServiceCollectionExtensions).Assembly));

    [Fact(DisplayName = "Настройки: выбор карточками «Что нужно», свободный режим разрешён (для «Помощи с текстом»), но не первый; без поиска по смыслу; история длиннее — для работы над текстом")]
    public void Settings_offer_assistant_cards()
    {
        var settings = _profile.Settings;

        settings.AreaCards.ShouldBeTrue();
        settings.AreaLabel.ShouldBe("Что нужно");
        settings.AllowFreeMode.ShouldBeTrue();
        settings.StartInFreeMode.ShouldBeFalse();
        settings.FreeAnswerLabel.ShouldBe("Без документов — проверьте сами");
        settings.AllowSenseSearch.ShouldBeFalse();
        settings.GroundedModeLabel.ShouldBe("По документам");
        settings.TopK.ShouldBe(10);
        settings.HistoryTurns.ShouldBe(10);
        settings.HistoryTurnLength.ShouldBe(4_000);
    }

    [Fact(DisplayName = "«Ответ по документам» — область по умолчанию на весь корпус: только по материалам, без фильтра, без грифа вопросов, без ключей уничтожения; иной ключ — нет области; право — у каждого вошедшего")]
    public async Task Default_area_is_whole_corpus()
    {
        var areas = await _profile.ListAreasAsync(Access);

        areas.Select(a => a.Key).ShouldBe([null, InspectorChatProfile.TextAreaKey, InspectorChatProfile.HelpAreaKey]);
        areas.Select(a => a.Title).ShouldBe(["Ответ по документам", "Помощь с текстом", "Как пользоваться системой"]);
        areas.ShouldAllBe(a => a.Hint != null && a.OnlyMode != null);
        var area = areas[0];
        area.Key.ShouldBeNull();
        area.Classification.ShouldBe((short)0);
        area.AuditClassification.ShouldBe(Access.MaxClassification, "запись журнала о вопросе — не ниже допуска сотрудника");
        area.AuditRef.ShouldBe(InspectorChatProfile.AuditRef);
        area.ScopeKeys.ShouldBeEmpty();
        area.OnlyMode.ShouldBe(ChatMode.Grounded);
        area.Icon.ShouldBe(ChatAreaIcon.Documents);
        area.AnswerLabel.ShouldBe("По документам");
        (await _profile.ResolveAreaAsync(null, Access)).ShouldBe(area);
        (await _profile.ResolveAreaAsync("case:3", Access)).ShouldBeNull();
        (await _profile.CanUseAsync()).ShouldBeTrue();

        var scope = await _profile.ResolveScopeAsync(area, Access, forQuestion: true);
        scope.DocumentIds.ShouldBeNull();
        scope.Unavailable.ShouldBeNull();
        scope.Materials.ShouldBeNull();
        _profile.TaskPrompt(area).ShouldBeNull();
        _profile.Suggestions(area).Free.ShouldNotBeEmpty();
        _profile.Suggestions(area).Grounded.ShouldNotBeEmpty();
        _profile.Suggestions(area).Sense.ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Помощник «Как пользоваться системой»: только вопросы, материалы — все разделы справки без грифа вместо поиска, источник — раздел и его страница, промпт из шаблона, свои подсказки")]
    public async Task Help_area_answers_from_user_guide()
    {
        var area = (await _profile.ResolveAreaAsync(InspectorChatProfile.HelpAreaKey, Access)).ShouldNotBeNull();
        area.OnlyMode.ShouldBe(ChatMode.Grounded);
        area.Icon.ShouldBe(ChatAreaIcon.Help);
        area.AnswerLabel.ShouldBe("По справке");
        area.Classification.ShouldBe((short)0);
        area.AuditClassification.ShouldBe(Access.MaxClassification, "в вопрос могли вписать лишнее — запись журнала по допуску");
        area.AuditRef.ShouldBe("inspector:help");
        area.Link.ShouldNotBeNull().Href.ShouldBe("/help");

        var scope = await _profile.ResolveScopeAsync(area, Access, forQuestion: true);
        var materials = scope.Materials.ShouldNotBeNull();
        materials.Count.ShouldBe(UserGuide.Current.Sections.Count);
        materials.ShouldAllBe(m => m.Classification == 0 && m.IsCurrent);
        materials.Select(m => m.ChunkId).ShouldBeUnique();
        scope.DocumentIds.ShouldBeNull();

        // Источник ответа — раздел справки и его страница; общий раздел без страницы ведёт на «Помощь».
        var generator = materials.Single(m => m.Text.StartsWith("Раздел справки: Генератор", StringComparison.Ordinal));
        generator.Text.ShouldContain("1. ");
        scope.Describe(generator).ShouldBe(new ChatSourceRef("справка", "Генератор", "/generator"));
        var common = materials.First(m => !m.Text.Contains("Адрес страницы:", StringComparison.Ordinal));
        scope.Describe(common).ShouldNotBeNull().Href.ShouldBe("/help");
        InspectorChatProfile.DescribeHelp(new RetrievedChunk(1, 60, "чужой фрагмент", 0, 1, true, 0.1)).ShouldBeNull();

        var prompt = _profile.TaskPrompt(area).ShouldNotBeNull();
        prompt.ShouldContain("ИнспекторAI");
        prompt.ShouldContain("только по фрагментам справки", Case.Insensitive);
        prompt.ShouldNotContain("{{");

        _profile.Suggestions(area).Grounded.ShouldNotBeEmpty();
        _profile.Suggestions(area).Free.ShouldBeEmpty();
        _profile.Suggestions(area).Sense.ShouldBeEmpty();
    }

    [Fact(DisplayName = "«Помощь с текстом»: только свободный режим, своя история и объект журнала, запись журнала по допуску; документов не видит — так и сказано; подсказки — работа с текстом")]
    public async Task Text_area_is_free_only()
    {
        var area = (await _profile.ResolveAreaAsync(InspectorChatProfile.TextAreaKey, Access)).ShouldNotBeNull();
        area.OnlyMode.ShouldBe(ChatMode.Free);
        area.Icon.ShouldBe(ChatAreaIcon.Text);
        area.AuditRef.ShouldBe("inspector:text");
        area.AuditClassification.ShouldBe(Access.MaxClassification);
        area.Hint.ShouldNotBeNull().ShouldContain("не видит");

        var scope = await _profile.ResolveScopeAsync(area, Access, forQuestion: false);
        scope.Coverage.ShouldBe("Документы не видит");
        scope.Materials.ShouldBeNull();
        _profile.TaskPrompt(area).ShouldBeNull();
        _profile.Suggestions(area).Free.ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Вопрос не туда: закон в «Помощи с текстом» и в помощнике по системе — к документам; «перепиши» в документах — к тексту; «пароль» — к помощнику по системе; обычные вопросы не останавливаются")]
    public async Task Redirects_point_to_the_right_assistant()
    {
        var areas = (await _profile.ListAreasAsync(Access)).ToDictionary(a => a.Key ?? string.Empty);
        static string? Target(ChatArea area, string question) =>
            area.Redirects?.FirstOrDefault(r => r.Matches(question)) is { } redirect ? redirect.AreaKey ?? "docs" : null;

        Target(areas["text"], "Какой испытательный срок по Трудовому кодексу?").ShouldBe("docs");
        Target(areas["text"], "Что сказано в статье 5?").ShouldBe("docs");
        Target(areas["help"], "Какая ответственность за разглашение тайны?").ShouldBe("docs");
        Target(areas[string.Empty], "Перепиши этот абзац строже").ShouldBe(InspectorChatProfile.TextAreaKey);
        Target(areas[string.Empty], "Как сменить пароль?").ShouldBe(InspectorChatProfile.HelpAreaKey);
        Target(areas["text"], "Где кнопка экспорта в Word?").ShouldBe(InspectorChatProfile.HelpAreaKey);

        Target(areas[string.Empty], "Какие полномочия у прокурора при проверке?").ShouldBeNull();
        Target(areas["text"], "Составь вывод по итогам проверки отдела кадров").ShouldBeNull();
        Target(areas["text"], "Всё нормально, сделай текст короче и вежливее").ShouldBeNull();
        Target(areas["help"], "Как подготовить справку по итогам проверки?").ShouldBeNull();
    }

    [Fact(DisplayName = "Источник: документ документооборота — «№ рег. номер · заголовок» и его карточка; документ корпуса — заголовок и карточка корпуса; без заголовка — номер документа")]
    public void Sources_are_labelled_and_linked()
    {
        var docflow = new RetrievedChunk(1, 50, "текст", 1, 1, true, 0.1,
            new Dictionary<string, string> { ["docflow_document_id"] = "77", ["reg_number"] = "12-3/2026" }, "Справка о проверке");
        InspectorChatProfile.Describe(docflow).ShouldBe(new ChatSourceRef("документ", "№ 12-3/2026 · Справка о проверке", "/docflow/documents/77"));

        var unregistered = docflow with { Metadata = new Dictionary<string, string> { ["docflow_document_id"] = "78" } };
        InspectorChatProfile.Describe(unregistered).Label.ShouldBe("Справка о проверке");

        var npa = new RetrievedChunk(2, 60, "статья 5", 0, 1, true, 0.2, null, "  Закон о службе  ");
        InspectorChatProfile.Describe(npa).ShouldBe(new ChatSourceRef("документ корпуса", "Закон о службе", "/npa/documents/60"));

        var untitled = new RetrievedChunk(3, 61, "текст", 0, 1, true, 0.2);
        InspectorChatProfile.Describe(untitled).Label.ShouldBe("Документ № 61");

        var broken = new RetrievedChunk(4, 62, "текст", 0, 1, true, 0.2, new Dictionary<string, string> { ["docflow_document_id"] = "не число" }, "Т");
        InspectorChatProfile.Describe(broken).Href.ShouldBe("/npa/documents/62");
    }

    [Fact(DisplayName = "Профиль: страница чата — из пакета, «Чат-ассистент» в «ИИ-инструментах»; порт и настройки зарегистрированы")]
    public void Profile_wires_chat_module()
    {
        var profile = new InspectorProfile();

        var chat = profile.Modules.Single(m => m.Route == ChatModule.Route);
        chat.Id.ShouldBe("chat");
        chat.MenuTitle.ShouldBe("Чат-ассистент");
        chat.MenuGroup.ShouldBe("ИИ-инструменты");
        chat.RequiredPolicy.ShouldBe(ChatModule.ReadPolicy);
        chat.ComponentType.Assembly.GetName().Name.ShouldBe("ISC.AI.Modules.Chat.UI");

        var services = new ServiceCollection();
        services.AddInspectorApplication();
        ChatModule.RegisterServices(services);
        services.Single(d => d.ServiceType == typeof(IChatProfile)).ImplementationType.ShouldBe(typeof(InspectorChatProfile));
        services.ShouldContain(d => d.ServiceType == typeof(ChatSettings));
    }
}

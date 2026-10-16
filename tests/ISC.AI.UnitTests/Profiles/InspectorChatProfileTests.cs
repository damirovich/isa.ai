using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat;
using ISC.AI.Modules.Chat.Domain.Services;
using ISC.AI.Profile.Inspector;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Chat;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// «Чат-ассистент» «ИнспектораAI» на пакете «Чат» (ADR-0052): одна область — весь корпус по допуску, свободный режим по
/// умолчанию и «По документам» без задачного промпта (как до пакета), без поиска по смыслу; источник — карточка
/// документа документооборота или карточка документа корпуса; страница — из пакета, в «ИИ-инструментах».
/// </summary>
public sealed class InspectorChatProfileTests
{
    private static readonly AccessContext Access = new("7", 2, [1]);
    private readonly InspectorChatProfile _profile = new();

    [Fact(DisplayName = "Настройки: свободный режим включён и первый, «По документам», без поиска по смыслу; история длиннее — для работы над текстом")]
    public void Settings_keep_previous_behaviour()
    {
        var settings = _profile.Settings;

        settings.AllowFreeMode.ShouldBeTrue();
        settings.StartInFreeMode.ShouldBeTrue();
        settings.AllowSenseSearch.ShouldBeFalse();
        settings.GroundedModeLabel.ShouldBe("По документам");
        settings.TopK.ShouldBe(10);
        settings.HistoryTurns.ShouldBe(10);
        settings.HistoryTurnLength.ShouldBe(4_000);
    }

    [Fact(DisplayName = "Одна область по умолчанию — весь корпус: без фильтра, без грифа вопросов, без ключей уничтожения; иной ключ — нет области; право — у каждого вошедшего")]
    public async Task Single_area_is_whole_corpus()
    {
        var areas = await _profile.ListAreasAsync(Access);

        var area = areas.ShouldHaveSingleItem();
        area.Key.ShouldBeNull();
        area.Classification.ShouldBe((short)0);
        area.AuditClassification.ShouldBe(Access.MaxClassification, "запись журнала о вопросе — не ниже допуска сотрудника");
        area.AuditRef.ShouldBe(InspectorChatProfile.AuditRef);
        area.ScopeKeys.ShouldBeEmpty();
        (await _profile.ResolveAreaAsync(null, Access)).ShouldBe(area);
        (await _profile.ResolveAreaAsync("case:3", Access)).ShouldBeNull();
        (await _profile.CanUseAsync()).ShouldBeTrue();

        var scope = await _profile.ResolveScopeAsync(area, Access, forQuestion: true);
        scope.DocumentIds.ShouldBeNull();
        scope.Unavailable.ShouldBeNull();
        _profile.TaskPrompt(area).ShouldBeNull();
        _profile.Suggestions(area).Free.ShouldNotBeEmpty();
        _profile.Suggestions(area).Grounded.ShouldNotBeEmpty();
        _profile.Suggestions(area).Sense.ShouldBeEmpty();
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

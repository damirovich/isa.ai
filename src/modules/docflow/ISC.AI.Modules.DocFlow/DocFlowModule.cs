using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.DocFlow.Application;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace ISC.AI.Modules.DocFlow;

/// <summary>
/// Манифест пакета модулей «Документооборот» (ADR-0017) — переиспользуемая вертикаль, которую ПРОФИЛЬ
/// включает в свой реестр модулей (<c>IProfile.Modules</c>).
/// </summary>
/// <remarks>
/// Это НЕ профиль: профиль в развёртывании ровно один (<c>IProfile</c>, ТС-004/006), а документооборот
/// нужен и «ИнспекторAI», и любому будущему профилю-эксплуатанту, поэтому оформлен отдельным пакетом.
/// ИНВАРИАНТ: пакет НЕ зависит ни от одного профиля и ни от хоста — иначе переиспользование теряется
/// (проверяется архитектурным тестом <c>DependencyRulesTests</c>).
/// Порядок вызова со стороны профиля повторяет порядок композиции хоста (ТО-прог-05):
/// <see cref="RegisterServices"/> → <see cref="RegisterDataContexts"/>.
/// </remarks>
public static class DocFlowModule
{
    /// <summary>Секция меню, под которой профиль группирует страницы документооборота.</summary>
    public const string MenuGroup = "Документооборот";

    /// <summary>
    /// Политика доступа страниц модуля. Пока — «аутентифицирован» (регистрируется хостом из реестра);
    /// ролевое разграничение по ТЗ СКИД §2.1 (справочники — Администратор, построчные правила — через
    /// <c>IAccessPolicy</c>) — этап 6 Э4-35.
    /// </summary>
    public const string ReadPolicy = "docflow.read";

    /// <summary>
    /// Реестр страниц модуля — профиль подмешивает его в свой <c>IProfile.Modules</c>.
    /// Этап 1 Э4-35: справочник типов; остальные страницы СКИД — этап 5.
    /// </summary>
    public static IReadOnlyList<IModule> Modules { get; } =
    [
        new ModuleDescriptor("docflow-documents", "/docflow/documents", "Документы",
            Icons.Material.Filled.Description, typeof(Documents), ReadPolicy, MenuGroup),
        new ModuleDescriptor("docflow-types", "/docflow/types", "Типы документов",
            Icons.Material.Filled.Category, typeof(DocumentTypes), ReadPolicy, MenuGroup),
    ];

    /// <summary>Прикладные сервисы модуля — вызывается профилем в <c>IProfile.RegisterServices</c>.</summary>
    public static IServiceCollection RegisterServices(IServiceCollection services) =>
        services.AddDocFlowApplication();

    /// <summary>Контексты данных модуля — вызывается профилем в <c>IProfile.RegisterDataContexts</c>.</summary>
    public static IServiceCollection RegisterDataContexts(
        IServiceCollection services, IConfiguration configuration) =>
        services.AddDocFlowPersistence(configuration);
}

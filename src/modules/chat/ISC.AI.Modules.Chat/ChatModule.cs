using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.Chat.Application;
using ISC.AI.Modules.Chat.UI;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Chat;

/// <summary>
/// Манифест пакета модулей «Чат» (ADR-0052): вопросы локальной модели к материалам профиля с ответом только по
/// найденному и ссылками на источники, свободный режим и поиск по смыслу — одинаково для любого профиля. Профиль ставит
/// страницу в своё меню (<see cref="Page"/>) и закрывает один порт — <c>IChatProfile</c>.
/// </summary>
/// <remarks>
/// Почему пакет, а не копия в каждом профиле: чат был сделан дважды («Чат-ассистент» «ИнспектораAI» и «Чат по
/// материалам» «Следствия») с одинаковыми экраном, журналом и проверкой ссылок, а различались только область поиска и
/// подписи источников. Третья копия означала бы три расходящихся набора проверок доступа на одно действие.
///
/// Слоя данных у пакета НЕТ: диалоги — в таблицах ядра <c>core.conversation</c> через <c>IConversationStore</c>
/// (там же остались диалоги «ИнспектораAI», заведённые до пакета). Поэтому <c>RegisterDataContexts</c> здесь нет.
/// </remarks>
public static class ChatModule
{
    /// <summary>Политика доступа страницы чата («аутентифицирован» — регистрируется хостом из реестра профиля).</summary>
    public const string ReadPolicy = "chat.read";

    /// <summary>Идентификатор записи реестра (по нему профиль решает видимость пункта меню).</summary>
    public const string ModuleId = "chat";

    /// <summary>Маршрут страницы чата.</summary>
    public const string Route = "/chat";

    /// <summary>
    /// ЧТО ПРОФИЛЬ ОБЯЗАН ПРЕДОСТАВИТЬ. Без порта контейнер не соберёт обработчик и приложение не запустится —
    /// намеренно: чат без правила области и решётки опаснее остановки.
    /// </summary>
    /// <remarks>
    /// <c>IChatProfile</c> — настройки, право, области, материалы области с подписями источников, задачный промпт и
    /// подсказки. Список закреплён тестом <c>ChatContractTests</c>: он не должен разрастаться незаметно.
    /// </remarks>
    public static IReadOnlyList<Type> RequiredServices { get; } =
    [
        typeof(Domain.Services.IChatProfile),
    ];

    /// <summary>
    /// Нейтральные службы ядра, которыми пакет пользуется и которые даёт хост: контекст доступа, хранилище диалогов,
    /// поиск фрагментов, грунтованная и свободная генерация, писатель журнала.
    /// </summary>
    public static IReadOnlyList<Type> RequiredCoreServices { get; } =
    [
        typeof(Abstractions.Security.IAccessContextProvider),
        typeof(Abstractions.Conversations.IConversationStore),
        typeof(Abstractions.Retrieval.IRetriever),
        typeof(Abstractions.Rag.IGroundedGenerator),
        typeof(Abstractions.Rag.IConversationalGenerator),
        typeof(Abstractions.Audit.IAuditWriter),
    ];

    /// <summary>
    /// Страница чата для реестра профиля: название пункта меню, секция и значок — профиля; маршрут, политика и
    /// идентификатор — пакета. Название и значок становятся и заголовком страницы.
    /// </summary>
    /// <param name="menuTitle">Название пункта меню и заголовок страницы («Чат-ассистент», «Чат по материалам»).</param>
    /// <param name="menuGroup">Секция меню профиля.</param>
    /// <param name="menuIcon">Значок (константа MudBlazor).</param>
    public static IModule Page(string menuTitle, string menuGroup, string menuIcon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(menuTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(menuGroup);
        ArgumentException.ThrowIfNullOrWhiteSpace(menuIcon);
        return new ModuleDescriptor(ModuleId, Route, menuTitle, menuIcon, typeof(ChatPage), ReadPolicy, menuGroup);
    }

    /// <summary>Прикладные сервисы пакета — вызывается профилем в <c>IProfile.RegisterServices</c>.</summary>
    public static IServiceCollection RegisterServices(IServiceCollection services) =>
        services.AddChatApplication();
}

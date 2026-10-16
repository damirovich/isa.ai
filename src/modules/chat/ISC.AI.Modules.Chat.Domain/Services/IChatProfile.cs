using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Chat.Domain.Services;

/// <summary>
/// Порт профиля для пакета «Чат» (ADR-0052): то, чего пакет знать не может, — где спрашивать, что из корпуса видно
/// сотруднику в выбранной области, как подписать источник, какой задачный промпт и какие подсказки. Всё остальное —
/// диалоги, журнал, грунтовка, проверка ссылок, экран — пакет делает одинаково для всех профилей.
/// </summary>
/// <remarks>
/// <para>ИНВАРИАНТЫ, которые обязан держать профиль (ТБ-020/021/071):</para>
/// <list type="bullet">
/// <item><see cref="ResolveAreaAsync"/> отдаёт область ТОЛЬКО если она видна субъекту; иначе <see langword="null"/> —
/// неотличимо от несуществующей. Пакет вызывает его на каждом обращении к диалогу, поэтому потеря доступа к области
/// (например, к делу) сразу закрывает и её диалоги.</item>
/// <item><see cref="ResolveScopeAsync"/> отдаёт только материалы под решётками профиля и подключённых пакетов. Поверх
/// области ядро ещё раз применяет фильтр допуска к фрагментам — область лишь СУЖАЕТ выдачу, не расширяет.</item>
/// <item><see cref="TaskPrompt"/> — второе системное сообщение после неотключаемого правила грунтовки ядра; ослабить
/// его профиль не может (ТБ-041). Промпт — из файла-шаблона профиля, не строка в коде.</item>
/// </list>
/// </remarks>
public interface IChatProfile
{
    /// <summary>Настройки чата профиля: режимы, подписи, пределы. Без обращений к данным.</summary>
    ChatSettings Settings { get; }

    /// <summary>Вправе ли вошедший сотрудник пользоваться чатом (роль, матрица доступа профиля).</summary>
    Task<bool> CanUseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Области, в которых сотрудник может спрашивать, — для выбора на странице. Первая — область по умолчанию
    /// (ключ <see langword="null"/>).
    /// </summary>
    Task<IReadOnlyList<ChatArea>> ListAreasAsync(AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Область по ключу (<see langword="null"/> — область по умолчанию); <see langword="null"/> — ключ неизвестен или
    /// область субъекту не видна (ТБ-021).
    /// </summary>
    Task<ChatArea?> ResolveAreaAsync(string? areaKey, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Материалы области. <paramref name="forQuestion"/> — перед вопросом или поиском: профиль может подготовить
    /// материалы (положить в корпус изменившееся), в том числе обращаясь к модели эмбеддингов; без него — только
    /// строка «что видит чат» по уже готовому.
    /// </summary>
    /// <exception cref="ISC.AI.Abstractions.AI.ModelUnavailableException">Подготовка не смогла обратиться к модели.</exception>
    Task<ChatScope> ResolveScopeAsync(
        ChatArea area, AccessContext access, bool forQuestion, CancellationToken cancellationToken = default);

    /// <summary>Задачный промпт области из файла-шаблона профиля; <see langword="null"/> — только правило грунтовки ядра.</summary>
    string? TaskPrompt(ChatArea area);

    /// <summary>Подсказки — готовые вопросы для области (щелчок задаёт вопрос сразу).</summary>
    ChatSuggestions Suggestions(ChatArea area);
}

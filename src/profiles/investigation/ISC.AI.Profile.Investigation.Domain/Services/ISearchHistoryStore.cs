using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Выполненный поиск для записи в историю (ADR-0049).</summary>
/// <param name="Terms">Разобранный запрос.</param>
/// <param name="Kinds">Виды выдачи.</param>
/// <param name="Direction">Отдел или все.</param>
/// <param name="ResultCount">Сколько нашлось.</param>
/// <param name="Classification">Допуск сотрудника в момент поиска — гриф строки истории.</param>
public sealed record SearchHistoryDraft(SearchTerms Terms, SearchKinds Kinds, CaseDirection? Direction, int ResultCount, short Classification);

/// <summary>Запрос из истории для показа.</summary>
/// <param name="Id">Запись.</param>
/// <param name="Text">Запрос как ввели.</param>
/// <param name="Kinds">Виды выдачи.</param>
/// <param name="Direction">Отдел или все.</param>
/// <param name="ResultCount">Сколько нашлось в последний раз.</param>
/// <param name="IsPinned">Закреплён.</param>
/// <param name="SearchedAt">Когда искали в последний раз (UTC).</param>
public sealed record SearchHistoryRow(
    int Id, string Text, SearchKinds Kinds, CaseDirection? Direction, int ResultCount, bool IsPinned, DateTime SearchedAt);

/// <summary>Итог изменения истории.</summary>
public enum SearchHistoryWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Записи нет или она чужая (неотличимо, ТБ-021).</summary>
    NotFound = 1,

    /// <summary>Закреплённых уже предельное число.</summary>
    PinLimit = 2,
}

/// <summary>
/// История единого поиска сотрудника (ADR-0049). Каждая операция — только над записями САМОГО сотрудника
/// (<paramref name="userId"/> во всех методах — вошедший, его определяет вызывающий); чтение — ещё и в пределах
/// текущего допуска (гриф строки ≤ <c>maxClassification</c>).
/// </summary>
public interface ISearchHistoryStore
{
    /// <summary>Сколько последних незакреплённых запросов хранится.</summary>
    public const int MaxRecent = 30;

    /// <summary>Сколько запросов можно закрепить.</summary>
    public const int MaxPinned = 20;

    /// <summary>
    /// Записать поиск: тот же запрос (ключ без учёта регистра и «ё/е», те же виды и отдел) поднимается наверх с новым
    /// временем и числом найденного; незакреплённые сверх <see cref="MaxRecent"/> вытесняются.
    /// </summary>
    Task RecordAsync(int userId, SearchHistoryDraft draft, CancellationToken cancellationToken = default);

    /// <summary>История сотрудника: закреплённые, затем недавние (новые первыми) — в пределах текущего допуска.</summary>
    Task<IReadOnlyList<SearchHistoryRow>> ListAsync(int userId, short maxClassification, CancellationToken cancellationToken = default);

    /// <summary>Закрепить или открепить свой запрос.</summary>
    Task<SearchHistoryWriteResult> SetPinnedAsync(int userId, int entryId, bool pinned, CancellationToken cancellationToken = default);

    /// <summary>Удалить свой запрос из истории.</summary>
    Task<SearchHistoryWriteResult> DeleteAsync(int userId, int entryId, CancellationToken cancellationToken = default);

    /// <summary>Очистить историю: удаляются незакреплённые запросы; возвращает, сколько удалено.</summary>
    Task<int> ClearAsync(int userId, CancellationToken cancellationToken = default);
}

using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Очередь Руководителя «Конфликты обмена» (ТФ-ОБМ-02, ТФ-АДМ-05, ADR-0068 v0.3): конфликты приёма пакетов из других
/// офисов и решения по ним — объединить, создать новое, пропустить. Решение только записывается; применяет его следующий
/// приём пакета ключом хранителя.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ДОСТУПА (ТБ-020/021, ТБ-079): строки — только в пределах допуска сотрудника (гриф и подразделение записи);
/// недоступный конфликт неотличим от несуществующего. Право на очередь (<c>exchange.conflicts</c>) проверяет слой
/// приложения; Администратору оно по умолчанию не выдаётся — решение по содержанию не его.
/// </remarks>
public interface IExchangeConflicts
{
    /// <summary>
    /// Конфликты в пределах допуска: ждущие решения и решённые, но не применённые; с <paramref name="includeHistory"/> —
    /// ещё применённые и снятые (последние).
    /// </summary>
    Task<IReadOnlyList<ExchangeConflictRow>> ListAsync(AccessContext access, bool includeHistory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Записывает решение Руководителя (до применения его можно изменить). «Создать новое» — только для дела; при совпадении
    /// номера в подразделении нужен новый незанятый номер.
    /// </summary>
    Task<ExchangeDecisionResult> DecideAsync(
        int conflictId,
        ExchangeConflictDecision decision,
        string? newNumber,
        string? comment,
        AccessContext access,
        CancellationToken cancellationToken = default);
}

/// <summary>Строка очереди «Конфликты обмена».</summary>
/// <param name="Id">Номер конфликта.</param>
/// <param name="Kind">Дело или носитель.</param>
/// <param name="SourceInstance">Офис-источник.</param>
/// <param name="PackageName">Пакет, которым конфликт найден (последний).</param>
/// <param name="FoundAt">Когда найден впервые (UTC).</param>
/// <param name="Label">Номер дела или имя файла источника.</param>
/// <param name="Reason">Чем совпало.</param>
/// <param name="Details">Описание записи источника.</param>
/// <param name="LocalId">Своя совпавшая запись.</param>
/// <param name="LocalLabel">Своё дело словами — если оно доступно сотруднику; иначе <see langword="null"/>.</param>
/// <param name="Status">Состояние.</param>
/// <param name="Decision">Решение.</param>
/// <param name="DecisionNumber">Номер нового дела.</param>
/// <param name="DecisionComment">Комментарий к решению.</param>
/// <param name="DecidedByUserId">Кто решил.</param>
/// <param name="DecidedAt">Когда решено (UTC).</param>
/// <param name="AppliedAt">Когда применено приёмом (UTC).</param>
/// <param name="NumberClash">
/// Номер дела источника в подразделении сейчас занят — «создать новое» требует нового номера (по состоянию базы, а не по
/// тексту причины).
/// </param>
public sealed record ExchangeConflictRow(
    int Id,
    ExchangeConflictKind Kind,
    string SourceInstance,
    string? PackageName,
    DateTime FoundAt,
    string? Label,
    string Reason,
    string? Details,
    int? LocalId,
    string? LocalLabel,
    ExchangeConflictStatus Status,
    ExchangeConflictDecision? Decision,
    string? DecisionNumber,
    string? DecisionComment,
    int? DecidedByUserId,
    DateTime? DecidedAt,
    DateTime? AppliedAt,
    bool NumberClash)
{
    /// <summary>Решение ещё можно принять или изменить.</summary>
    public bool CanDecide => Status is ExchangeConflictStatus.Pending or ExchangeConflictStatus.Decided;
}

/// <summary>Итог записи решения.</summary>
/// <param name="Ok">Решение записано.</param>
/// <param name="NotFound">Конфликт не найден или недоступен (неотличимо, ТБ-021).</param>
/// <param name="Message">Почему не записано — для сотрудника.</param>
public sealed record ExchangeDecisionResult(bool Ok, bool NotFound = false, string? Message = null)
{
    /// <summary>Записано.</summary>
    public static ExchangeDecisionResult Done { get; } = new(true);

    /// <summary>Не найдено или недоступно.</summary>
    public static ExchangeDecisionResult Missing { get; } = new(false, true, "Конфликт не найден или недоступен.");

    /// <summary>Отказ с причиной.</summary>
    public static ExchangeDecisionResult Refused(string message) => new(false, false, message);
}

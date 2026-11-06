using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Очередь Руководителя «Конфликты обмена» (ТФ-ОБМ-02, ТФ-АДМ-05, ADR-0068 v0.3–v0.4): конфликты приёма пакетов из других
/// офисов и решения по ним. Дело и носитель — объединить, создать новое, пропустить: решение только записывается, применяет
/// его следующий приём пакета ключом хранителя. Конфликт изменений принятой записи (v0.4) — принять версию источника или
/// оставить свою: решение применяется сразу (значения источника уже в строке конфликта).
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
    /// номера в подразделении нужен новый незанятый номер. По конфликту изменений решение применяется сразу.
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
/// <param name="RecordKind">Вид записи конфликта изменений (v0.4); у конфликтов дела и носителя — <c>None</c>.</param>
/// <param name="Changes">Поля конфликта изменений (v0.4): было, у источника, у получателя.</param>
/// <param name="LocalOwnerId">
/// Где своя запись конфликта изменений открывается (v0.4): объект реквизита, событие участника; у прочих — <see langword="null"/>.
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
    bool NumberClash,
    ExchangeRecordKind RecordKind = ExchangeRecordKind.None,
    IReadOnlyList<ExchangeFieldChange>? Changes = null,
    int? LocalOwnerId = null)
{
    /// <summary>Решение ещё можно принять или изменить.</summary>
    public bool CanDecide => Status is ExchangeConflictStatus.Pending or ExchangeConflictStatus.Decided;

    /// <summary>Конфликт изменений принятой записи (ADR-0068 v0.4) — решения «принять версию источника» / «оставить свою».</summary>
    public bool IsUpdate => Kind == ExchangeConflictKind.RecordUpdate;
}

/// <summary>
/// Поле конфликта изменений (ADR-0068 v0.4): значения в виде получателя (номера справочников и учёток — свои) и текст для
/// экрана. <see cref="Base"/> — значение снимка, с которым запись последний раз сведена; <see langword="null"/> у записи,
/// принятой до версии 0.4 и изменённой после приёма, — «неизвестно» (<see cref="BaseKnown"/>).
/// </summary>
/// <param name="Key">Поле.</param>
/// <param name="Label">Поле словами.</param>
/// <param name="Base">Было (снимок).</param>
/// <param name="Source">У источника.</param>
/// <param name="Local">У получателя.</param>
/// <param name="BaseText">Было — текстом.</param>
/// <param name="SourceText">У источника — текстом.</param>
/// <param name="LocalText">У получателя — текстом.</param>
/// <param name="BaseKnown">Снимок известен.</param>
public sealed record ExchangeFieldChange(
    string Key,
    string Label,
    string? Base,
    string? Source,
    string? Local,
    string? BaseText,
    string? SourceText,
    string? LocalText,
    bool BaseKnown = true);

/// <summary>Итог записи решения.</summary>
/// <param name="Ok">Решение записано.</param>
/// <param name="NotFound">Конфликт не найден или недоступен (неотличимо, ТБ-021).</param>
/// <param name="Message">Почему не записано — для сотрудника.</param>
/// <param name="AppliedNow">Решение уже применено (конфликт изменений, v0.4), повторного приёма не нужно.</param>
/// <param name="ClosedCaseId">
/// Решение закрыло своё дело (статус источника «закрыто», v0.4) — сценарию исполнить регламент закрытия (ТБ-074).
/// </param>
public sealed record ExchangeDecisionResult(
    bool Ok, bool NotFound = false, string? Message = null, bool AppliedNow = false, int? ClosedCaseId = null)
{
    /// <summary>Записано.</summary>
    public static ExchangeDecisionResult Done { get; } = new(true);

    /// <summary>Записано и применено сразу.</summary>
    public static ExchangeDecisionResult Applied { get; } = new(true, AppliedNow: true);

    /// <summary>Не найдено или недоступно.</summary>
    public static ExchangeDecisionResult Missing { get; } = new(false, true, "Конфликт не найден или недоступен.");

    /// <summary>Отказ с причиной.</summary>
    public static ExchangeDecisionResult Refused(string message) => new(false, false, message);
}

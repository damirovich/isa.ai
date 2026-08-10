using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

// Контракты НАЗНАЧЕНИЙ (ТЗ СКИД §4): черновик, участники, лента событий, итоги операций.
// Разрез по агрегату — см. DocumentContracts.cs.

/// <summary>Черновик назначения при регистрации (§4.1): подразделение + исполнитель + индивидуальный срок.</summary>
public sealed record AssignmentDraft(int DivisionId, int? AssigneeUserId, DateOnly? Deadline);

/// <summary>
/// Назначение со сроком в интересующем окне — исходные данные для уведомления (разд. 5 ТЗ).
/// Получатели: исполнитель назначения и инспектор документа.
/// </summary>
/// <remarks>
/// «Все Руководители» из СКИД в получатели НЕ входят: список ролей ведёт ПРОФИЛЬ (<c>inspector</c>),
/// а модуль документооборота на профиль не ссылается (ADR-0017). При необходимости добавляется портом,
/// реализацию которого даст профиль, — как сделано для справочника подразделений.
/// </remarks>
public sealed record DeadlineNotice(
    int AssignmentId,
    int DocumentId,
    string DocumentTitle,
    DateOnly Deadline,
    int? AssigneeUserId,
    int? InspectorUserId);

/// <summary>
/// Назначение, автоматически переведённое в «Просрочено» (§4.2), с грифом/подразделением его документа —
/// без них аудит перевода (см. <c>DeadlineCheckerJob</c>) классифицировал бы запись журнала грифом 0
/// независимо от реального грифа объекта (нарушение ТБ-032).
/// </summary>
public sealed record OverdueMark(
    int AssignmentId,
    short Classification,
    int DivisionId,
    int DocumentId,
    string DocumentTitle,
    DateOnly? Deadline,
    int? AssigneeUserId,
    int? InspectorUserId);

/// <summary>
/// Участники назначения и обозначение его документа — исходные данные уведомлений о СОБЫТИЯХ
/// (смена статуса, продление; разд. 5 ТЗ). Отдельно от <see cref="DeadlineNotice"/>: там срок есть
/// всегда (по нему и шёл отбор), здесь может отсутствовать, зато нужен контролёр.
/// </summary>
public sealed record AssignmentParticipants(
    int AssignmentId,
    int DocumentId,
    string DocumentTitle,
    DateOnly? Deadline,
    int? AssigneeUserId,
    int? InspectorUserId,
    int? ControllerUserId);

/// <summary>Вид события в ленте назначения (§4.8).</summary>
public enum AssignmentEventKind
{
    /// <summary>Назначение создано (переход «ниоткуда» в «Зарегистрировано»).</summary>
    Created,

    /// <summary>Смена статуса (§4.2), в том числе системный перевод в «Просрочено».</summary>
    StatusChanged,

    /// <summary>Продление срока (§4.6).</summary>
    DeadlineExtended,

    /// <summary>Смена исполнителя (§4.7). В СКИД в ленту НЕ попадала — см. AssignmentReassignment.</summary>
    Reassigned,
}

/// <summary>
/// Файл события ленты. <paramref name="Category"/> нужен для ссылки просмотра/скачивания: файлы
/// переходов и файлы-обоснования продлений лежат в РАЗНЫХ категориях хранилища.
/// </summary>
public sealed record AssignmentTimelineFile(
    int Id, string FileName, string ContentType, long FileSize, string StoredFileName, string Category);

/// <summary>
/// Событие ленты назначения (§4.8): что произошло, когда, кто инициировал и с чем.
/// </summary>
/// <remarks>
/// Лента СВОДИТ два источника — переходы статусов и продления сроков, — потому что для человека это
/// одна история назначения, а не две таблицы. <see cref="ActorUserId"/> = <see langword="null"/>
/// означает СИСТЕМУ (перевод в «Просрочено» ставит фоновая проверка, §4.2), и это не «неизвестно»:
/// показывать такое событие надо именно как системное.
/// </remarks>
public sealed record AssignmentTimelineEvent(
    AssignmentEventKind Kind,
    DateTime OccurredAt,
    int? ActorUserId,
    AssignmentStatus? FromStatus,
    AssignmentStatus? ToStatus,
    DateOnly? OldDeadline,
    DateOnly? NewDeadline,
    string? Comment,
    IReadOnlyList<AssignmentTimelineFile> Files,
    int? FromUserId = null,
    int? ToUserId = null);

/// <summary>
/// Итог добавления назначения к существующему документу (§4.1): статус, идентификатор и данные для
/// уведомлений — по тем же причинам, что у <see cref="CreatedDocumentNotice"/> (право исполнителя
/// получить уведомление не зависит от видимости документа для инициатора).
/// </summary>
public sealed record AssignmentAddResult(
    DocumentWriteStatus Status, int AssignmentId = 0, CreatedDocumentNotice? Notice = null);

/// <summary>Данные для уведомлений о смене исполнителя (§4.7, разд. 5).</summary>
public sealed record ReassignedNotice(
    int AssignmentId,
    int DocumentId,
    string DocumentTitle,
    int? PreviousAssigneeUserId,
    int NewAssigneeUserId,
    DateOnly? Deadline,
    int? InspectorUserId);

/// <summary>
/// Итог переназначения исполнителя. <see cref="Notice"/> = <see langword="null"/> при
/// <see cref="DocumentWriteStatus.Ok"/> означает «менять было нечего» (назначили того же человека) —
/// операция идемпотентна и уведомлений не порождает.
/// </summary>
public sealed record AssignmentReassignResult(DocumentWriteStatus Status, ReassignedNotice? Notice = null);

/// <summary>Назначение в карточке документа (§4.1): статус, срок, ответственные.</summary>
public sealed record AssignmentDetails(
    int Id,
    int DivisionId,
    int? AssigneeUserId,
    AssignmentStatus Status,
    DateOnly? Deadline,
    int? ControllerUserId);

using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

// Контракты ДОКУМЕНТА (ТЗ СКИД §3): черновики регистрации и правки, реестр, карточка.
// Разрез по агрегату — тем же швом, что partial-файлы DocumentStore (2026-08-10).

/// <summary>Черновик регистрации документа (ТЗ СКИД §3.2). Гриф и подразделение ОБЯЗАТЕЛЬНЫ (ADR-0017 п.5).</summary>
public sealed record DocumentDraft(
    string? RegNumber,
    DateOnly RegDate,
    int TypeId,
    DocumentDirection Direction,
    string? Source,
    string ShortContent,
    string? FullText,
    string? Notes,
    DocumentPriority? Priority,
    int? InspectorUserId,
    short Classification,
    int DivisionId,
    int? RegisteredByUserId);

/// <summary>
/// Правка реквизитов уже зарегистрированного документа (§3.2).
/// </summary>
/// <remarks>
/// Гриф и подразделение здесь ЕСТЬ, но правила для них строже, чем при регистрации, — см.
/// <see cref="IDocumentStore.UpdateAsync"/>: понизить гриф правкой нельзя, это рассекречивание.
/// Назначения этой операцией не меняются: у них свои сценарии (§4.1/§4.7).
/// </remarks>
public sealed record DocumentEdit(
    int DocumentId,
    string? RegNumber,
    DateOnly RegDate,
    int TypeId,
    DocumentDirection Direction,
    string? Source,
    string ShortContent,
    string? FullText,
    string? Notes,
    DocumentPriority? Priority,
    int? InspectorUserId,
    short Classification,
    int DivisionId);

/// <summary>
/// Итог правки документа: что изменилось, для аудита и уведомления о смене инспектора.
/// </summary>
/// <param name="ChangedFields">
/// Имена изменённых реквизитов (человекочитаемые). В журнал идут именно ИМЕНА, а не значения:
/// содержание документа под грифом не должно копиться второй копией в аудите (ТБ-032).
/// </param>
/// <param name="PreviousInspectorUserId">Инспектор ДО правки — по нему видно, была ли смена.</param>
public sealed record UpdatedDocumentNotice(
    int DocumentId,
    string DocumentTitle,
    short Classification,
    int DivisionId,
    IReadOnlyList<string> ChangedFields,
    int? PreviousInspectorUserId,
    int? InspectorUserId);

/// <summary>Результат правки документа.</summary>
public sealed record DocumentUpdateResult(DocumentWriteStatus Status, UpdatedDocumentNotice? Notice = null);

/// <summary>
/// Фильтры и постраничность реестра документов (§3.4).
/// </summary>
/// <param name="Text">Поиск по рег. номеру, краткому содержанию и источнику (без учёта регистра).</param>
/// <param name="Group">Группа типа: «Исполнение» или «Хранение».</param>
/// <param name="TypeId">Тип документа.</param>
/// <param name="AggregatedStatus">Агрегированный статус документа (§4.3).</param>
/// <param name="Priority">Приоритет (только у группы «Исполнение»).</param>
/// <param name="InspectorUserId">Ответственный инспектор.</param>
/// <param name="DivisionId">Подразделение-владелец документа.</param>
/// <param name="RegDateFrom">Начало периода регистрации включительно.</param>
/// <param name="RegDateTo">Конец периода регистрации включительно.</param>
/// <param name="Page">Номер страницы, с 1.</param>
/// <param name="PageSize">Размер страницы.</param>
public sealed record DocumentListFilter(
    string? Text = null,
    DocumentGroup? Group = null,
    int? TypeId = null,
    DocumentAggregatedStatus? AggregatedStatus = null,
    DocumentPriority? Priority = null,
    int? InspectorUserId = null,
    int? DivisionId = null,
    DateOnly? RegDateFrom = null,
    DateOnly? RegDateTo = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>
/// Страница реестра: строки и ОБЩЕЕ число подходящих документов.
/// </summary>
/// <remarks>
/// Общее число возвращается вместе со строками намеренно. Без него постраничная навигация не знает,
/// сколько страниц, а «показать ещё, пока не кончится» на реестре в тысячи документов означает, что
/// пользователь не может ни оценить объём выборки, ни попасть в её конец.
/// </remarks>
public sealed record DocumentPage(IReadOnlyList<DocumentListItem> Rows, int TotalCount);

/// <summary>Строка списка документов.</summary>
public sealed record DocumentListItem(
    int Id,
    string? RegNumber,
    DateOnly RegDate,
    string TypeName,
    DocumentGroup Group,
    DocumentDirection Direction,
    string ShortContent,
    DocumentPriority? Priority,
    DocumentAggregatedStatus AggregatedStatus,
    short Classification,
    int DivisionId,
    int AssignmentsCount);

/// <summary>Созданное назначение — минимум, нужный для уведомления «вам назначено» (§4.1, разд. 5).</summary>
public sealed record CreatedAssignmentNotice(int AssignmentId, int? AssigneeUserId, DateOnly? Deadline);

/// <summary>
/// Данные только что зарегистрированного документа для уведомлений (разд. 5): обозначение, инспектор
/// и созданные назначения.
/// </summary>
/// <remarks>
/// Возвращается ИЗ САМОЙ операции создания, а не читается потом отдельным запросом. Так сделано
/// намеренно, по двум причинам. (1) Корректность: право получить уведомление есть у ИСПОЛНИТЕЛЯ и
/// ИНСПЕКТОРА, и оно не должно зависеть от того, видит ли РЕГИСТРАТОР свой документ — а он может его
/// не видеть, если сужающая политика профиля (ADR-0014) построена не по грифу/подразделению, а по роли
/// (например, Исполнитель видит лишь документы со своим назначением). Перечитывание карточки допуском
/// автора молча съедало все уведомления такого документа. (2) Безопасность: отдельный метод «прочитать
/// документ без фильтра допуска по идентификатору» стал бы адресуемой дырой на чтение; здесь же данные
/// приходят из уже совершённой транзакции, и нового способа что-либо прочитать не появляется.
/// </remarks>
public sealed record CreatedDocumentNotice(
    int DocumentId,
    string DocumentTitle,
    int? InspectorUserId,
    IReadOnlyList<CreatedAssignmentNotice> Assignments);

/// <summary>Итог создания документа: статус, идентификатор и данные для уведомлений при успехе.</summary>
public sealed record DocumentCreateResult(
    DocumentWriteStatus Status, int DocumentId = 0, CreatedDocumentNotice? Notice = null);

/// <summary>Карточка документа (§3.2 + §4.8): атрибуты и назначения.</summary>
public sealed record DocumentDetails(
    int Id,
    string? RegNumber,
    DateOnly RegDate,
    int TypeId,
    string TypeName,
    DocumentGroup Group,
    DocumentDirection Direction,
    string? Source,
    string ShortContent,
    string? FullText,
    string? Notes,
    DocumentPriority? Priority,
    int? InspectorUserId,
    DocumentAggregatedStatus AggregatedStatus,
    short Classification,
    int DivisionId,
    IReadOnlyList<AssignmentDetails> Assignments,
    DateTime? IndexedAt,
    IReadOnlyList<DocumentFileItem> Files,
    IReadOnlyList<AttachmentItem> Attachments);

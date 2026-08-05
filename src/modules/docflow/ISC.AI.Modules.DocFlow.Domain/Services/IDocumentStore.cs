using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

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

/// <summary>Черновик назначения при регистрации (§4.1): подразделение + исполнитель + индивидуальный срок.</summary>
public sealed record AssignmentDraft(int DivisionId, int? AssigneeUserId, DateOnly? Deadline);

/// <summary>Фильтры списка документов (§3.4).</summary>
public sealed record DocumentListFilter(
    string? Text = null,
    int? TypeId = null,
    DocumentAggregatedStatus? AggregatedStatus = null,
    DateOnly? RegDateFrom = null,
    DateOnly? RegDateTo = null);

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

/// <summary>Итог операции хранилища документов (для маппинга в ответ сценария).</summary>
public enum DocumentWriteStatus
{
    /// <summary>Выполнено.</summary>
    Ok,

    /// <summary>Объект не найден.</summary>
    NotFound,

    /// <summary>Регистрационный номер уже занят (уникальность §3.2).</summary>
    RegNumberTaken,

    /// <summary>Тип не найден или неактивен (§3.1: неактивный тип не предлагается при регистрации).</summary>
    TypeUnavailable,

    /// <summary>Для группы «Исполнение» обязательны приоритет, инспектор и назначения (§3.2, §4.1).</summary>
    ExecutionFieldsMissing,

    /// <summary>Переход статуса не допускается матрицей §4.5.</summary>
    InvalidTransition,

    /// <summary>«Просрочено» вручную не назначается — только система по сроку (§4.2).</summary>
    OverdueIsAutomatic,

    /// <summary>У назначения нет срока — продлевать нечего (§4.6).</summary>
    NoDeadline,

    /// <summary>Конкурентное изменение (xmin) — повторить с актуальными данными.</summary>
    Conflict,
}

/// <summary>Итог создания документа: статус + идентификатор при успехе.</summary>
public sealed record DocumentCreateResult(DocumentWriteStatus Status, int DocumentId = 0);

/// <summary>Назначение в карточке документа (§4.1): статус, срок, ответственные.</summary>
public sealed record AssignmentDetails(
    int Id,
    int DivisionId,
    int? AssigneeUserId,
    AssignmentStatus Status,
    DateOnly? Deadline,
    int? ControllerUserId);

/// <summary>
/// Файл документа в карточке (§3.3): версия, актуальность, язык. <see cref="StoredFileName"/> и
/// <see cref="PdfCopyStoredFileName"/> — для построения ссылки просмотра/скачивания (этап 4.3).
/// </summary>
public sealed record DocumentFileItem(
    int Id, string FileName, DocumentLanguage Language, int Version, bool IsLatest,
    long FileSize, DateTime UploadedAt, string StoredFileName, string? PdfCopyStoredFileName);

/// <summary>Сопутствующий файл в карточке.</summary>
public sealed record AttachmentItem(int Id, string FileName, long FileSize, DateTime UploadedAt, string StoredFileName);

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

/// <summary>
/// Порт хранилища документов и назначений (ТЗ СКИД §3–4). Порт — в домене модуля, реализация — в слое
/// данных (та же слоистость, что <see cref="IDocumentTypeStore"/>). Файлы переходов/продлений
/// подключаются на этапе 4 Э4-35 (нужно хранилище файлов).
/// </summary>
public interface IDocumentStore
{
    /// <summary>
    /// Регистрирует документ (§3.2). Для группы «Исполнение» создаёт назначения со статусом
    /// «Зарегистрировано», историю и вычисляет агрегированный статус (§4.3); для «Хранения»
    /// назначения не создаются, приоритет/инспектор обнуляются (§1.4).
    /// </summary>
    Task<DocumentCreateResult> CreateAsync(
        DocumentDraft draft,
        IReadOnlyList<AssignmentDraft> assignments,
        bool useCommonDeadline,
        DateOnly? commonDeadline,
        CancellationToken cancellationToken = default);

    /// <summary>Список документов с фильтрами (§3.4), новые первыми.</summary>
    Task<IReadOnlyList<DocumentListItem>> ListAsync(
        DocumentListFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Карточка документа с назначениями; <see langword="null"/> — не найден.</summary>
    Task<DocumentDetails?> GetAsync(int documentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ручной переход статуса назначения (§4.2/4.5): матрица переходов, запрет ручного «Просрочено»,
    /// фиксация/сброс контролёра на входе/выходе «Контроль», запись истории, пересчёт агрегата документа.
    /// </summary>
    Task<DocumentWriteStatus> ChangeAssignmentStatusAsync(
        int assignmentId,
        AssignmentStatus newStatus,
        string? comment,
        int? changedByUserId,
        IReadOnlyList<UploadedFile>? files = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Загружает версионируемый файл документа (§3.3): прежняя версия того же языка теряет актуальность,
    /// новая получает <c>Version = max + 1</c> и <c>IsLatest</c>. Содержимое — в защищённое хранилище;
    /// при сбое записи в БД сохранённый файл компенсирующе удаляется.
    /// </summary>
    Task<DocumentWriteStatus> AddDocumentFileAsync(
        int documentId, UploadedFile file, DocumentLanguage language, int? uploadedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Прикрепляет сопутствующий файл (без версионирования).</summary>
    Task<DocumentWriteStatus> AddAttachmentAsync(
        int documentId, UploadedFile file, int? uploadedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Переводит в «Просрочено» все назначения с истёкшим сроком (§4.2: ставит ТОЛЬКО система).
    /// Кандидаты: срок &lt; <paramref name="today"/> и статус не Done/Closed/Overdue. Каждое — отдельной
    /// транзакцией (конкуренция одного не валит остальных); история — от системы (без пользователя);
    /// агрегаты затронутых документов пересчитываются. Возвращает идентификаторы переведённых.
    /// </summary>
    Task<IReadOnlyList<int>> MarkOverdueAsync(DateOnly today, CancellationToken cancellationToken = default);

    /// <summary>
    /// Продление срока назначения (§4.6): фиксируется старый/новый срок и основание; назначение
    /// автоматически возвращается «В работу» (с записью истории, если статус изменился).
    /// </summary>
    Task<DocumentWriteStatus> ExtendDeadlineAsync(
        int assignmentId,
        DateOnly newDeadline,
        string reason,
        int initiatedByUserId,
        IReadOnlyList<UploadedFile>? files = null,
        CancellationToken cancellationToken = default);
}

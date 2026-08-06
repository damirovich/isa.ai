using ISC.AI.Abstractions.Security;
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

    /// <summary>Превышен лимит сопутствующих вложений на документ (ТЗ §3.3.1, перенос СКИД DL-057).</summary>
    TooManyAttachments,

    /// <summary>
    /// Гриф/подразделение создаваемого документа вне допуска создающего (ТБ-020/021, этап 6.6):
    /// нельзя зарегистрировать документ выше собственного допуска — он тут же стал бы невидим автору.
    /// </summary>
    OutsideClearance,
}

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
/// Файл документа в карточке (§3.3): версия, актуальность, язык. <see cref="StoredFileName"/> —
/// для построения ссылки просмотра/скачивания (этап 4.3).
/// </summary>
public sealed record DocumentFileItem(
    int Id, string FileName, DocumentLanguage Language, int Version, bool IsLatest,
    long FileSize, DateTime UploadedAt, string StoredFileName);

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
        AccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Список документов с фильтрами (§3.4), новые первыми. Разграничение — НА ЭТАПЕ ВЫБОРКИ
    /// (инвариант 3, ТБ-020/021): выдаются только документы с грифом не выше допуска субъекта
    /// <paramref name="access"/> и из разрешённых ему подразделений; fail-closed — пустой список
    /// разрешённых подразделений даёт пустую выдачу, а не «все».
    /// </summary>
    Task<IReadOnlyList<DocumentListItem>> ListAsync(
        DocumentListFilter filter, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Карточка документа с назначениями; <see langword="null"/> — не найден. Документ вне допуска
    /// субъекта <paramref name="access"/> НЕ отличается от несуществующего (то же решение, что 404
    /// у раздачи файлов — сам факт существования не подтверждается, ТБ-020/021).
    /// </summary>
    Task<DocumentDetails?> GetAsync(
        int documentId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ручной переход статуса назначения (§4.2/4.5): матрица переходов, запрет ручного «Просрочено»,
    /// фиксация/сброс контролёра на входе/выходе «Контроль», запись истории, пересчёт агрегата документа.
    /// </summary>
    /// <remarks>Недоступное назначение неотличимо от несуществующего — см. <see cref="WriteAccessRule"/>.</remarks>
    Task<DocumentWriteStatus> ChangeAssignmentStatusAsync(
        int assignmentId,
        AssignmentStatus newStatus,
        string? comment,
        AccessContext access,
        IReadOnlyList<UploadedFile>? files = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Загружает версионируемый файл документа (§3.3): прежняя версия того же языка теряет актуальность,
    /// новая получает <c>Version = max + 1</c> и <c>IsLatest</c>. Содержимое — в защищённое хранилище;
    /// при сбое записи в БД сохранённый файл компенсирующе удаляется.
    /// </summary>
    /// <remarks>Недоступный документ неотличим от несуществующего — см. <see cref="WriteAccessRule"/>.</remarks>
    Task<DocumentWriteStatus> AddDocumentFileAsync(
        int documentId, UploadedFile file, DocumentLanguage language, AccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>Прикрепляет сопутствующий файл (без версионирования).</summary>
    /// <remarks>Недоступный документ неотличим от несуществующего — см. <see cref="WriteAccessRule"/>.</remarks>
    Task<DocumentWriteStatus> AddAttachmentAsync(
        int documentId, UploadedFile file, AccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Переводит в «Просрочено» все назначения с истёкшим сроком (§4.2: ставит ТОЛЬКО система).
    /// Кандидаты: срок &lt; <paramref name="today"/> и статус не Done/Closed/Overdue. Каждое — отдельной
    /// транзакцией (конкуренция одного не валит остальных); история — от системы (без пользователя);
    /// агрегаты затронутых документов пересчитываются. Возвращает переведённые назначения вместе
    /// с грифом/подразделением владеющего документа (для честного аудита перевода, ТБ-032).
    /// </summary>
    Task<IReadOnlyList<OverdueMark>> MarkOverdueAsync(DateOnly today, CancellationToken cancellationToken = default);

    /// <summary>
    /// Назначения, чей срок попадает в окно <paramref name="from"/>..<paramref name="until"/> включительно
    /// и которые ещё в работе (не «Исполнено»/«Снято»/«Просрочено») — кандидаты на уведомление о сроке.
    /// Только чтение; повторные отправки отсекает дедупликация <see cref="INotificationStore"/>.
    /// </summary>
    Task<IReadOnlyList<DeadlineNotice>> FindDeadlineNoticesAsync(
        DateOnly from, DateOnly until, CancellationToken cancellationToken = default);

    /// <summary>
    /// Участники назначения для уведомления о событии (разд. 5); <see langword="null"/> — назначения нет
    /// ИЛИ его документ недоступен субъекту <paramref name="access"/> (та же неразличимость, что
    /// у операций записи, см. <see cref="WriteAccessRule"/>).
    /// </summary>
    Task<AssignmentParticipants?> GetAssignmentParticipantsAsync(
        int assignmentId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Продление срока назначения (§4.6): фиксируется старый/новый срок и основание; назначение
    /// автоматически возвращается «В работу» (с записью истории, если статус изменился).
    /// </summary>
    /// <remarks>Недоступное назначение неотличимо от несуществующего — см. <see cref="WriteAccessRule"/>.</remarks>
    Task<DocumentWriteStatus> ExtendDeadlineAsync(
        int assignmentId,
        DateOnly newDeadline,
        string reason,
        AccessContext access,
        IReadOnlyList<UploadedFile>? files = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Правило доступа на ЗАПИСЬ (этап 6.6 Э4-35): «нельзя менять то, чего не видишь». Любая операция
/// записи по существующему объекту сперва проверяет, что документ-владелец ВИДЕН субъекту тем же
/// предикатом, что и чтение (решётка гриф/подразделение ТБ-020/021 + сужающая политика профиля
/// ADR-0014), и при недоступности возвращает <see cref="DocumentWriteStatus.NotFound"/> — тот же
/// ответ, что и для несуществующего объекта (существование чужого документа не подтверждается).
/// </summary>
/// <remarks>
/// Введено после проверки радиуса поражения (6.4.2): этап 6.4 сузил ЧТЕНИЕ, оставив запись открытой —
/// субъект, не видевший ни одного документа, мог зарегистрировать документ с любым грифом, залить
/// файл в чужой документ по идентификатору и сменить статус любого назначения. Прежнее обоснование
/// («из интерфейса не добраться») держалось лишь на том, что кнопку не рисуют.
/// Регистрация НОВОГО документа проверяется иначе — гриф/подразделение создаваемого документа обязаны
/// укладываться в допуск создающего (<see cref="DocumentWriteStatus.OutsideClearance"/>): нельзя
/// создать документ выше собственного допуска и тем самым «потерять» его из виду.
/// </remarks>
public static class WriteAccessRule;

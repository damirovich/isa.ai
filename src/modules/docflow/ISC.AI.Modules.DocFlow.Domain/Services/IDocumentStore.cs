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
    /// Гриф создаваемого документа ВЫШЕ допуска создающего (ТБ-020, этап 6.6): такой документ тут же
    /// стал бы невидим самому автору.
    /// </summary>
    /// <remarks>
    /// Отделён от <see cref="DivisionOutsideClearance"/> намеренно: единый ответ «гриф ИЛИ
    /// подразделение вне допуска» заставлял пользователя угадывать, какое из двух полей исправлять.
    /// Разглашения здесь нет — речь о СВОЁМ допуске и СВОЁМ вводе, чужие данные не раскрываются.
    /// </remarks>
    ClassificationOutsideClearance,

    /// <summary>
    /// Подразделение-владелец создаваемого документа НЕ входит в разрешённые создающему (ТБ-021,
    /// этап 6.6): документ тут же стал бы невидим самому автору.
    /// </summary>
    DivisionOutsideClearance,

    /// <summary>
    /// Назначения возможны только у документов группы «Исполнение» (§3.2/§4.1): у «Хранения» их нет
    /// по построению, и агрегированный статус такого документа обязан оставаться «неприменимо».
    /// </summary>
    NotExecutionGroup,

    /// <summary>
    /// У документа уже есть назначение на это подразделение (§4.1: одно подразделение — одно
    /// назначение). Перенос запрета дубля из СКИД, но с проверкой в БД, а не только в форме.
    /// </summary>
    AssignmentDivisionTaken,

    /// <summary>
    /// Исполнителю не разрешено подразделение назначения — он не увидел бы документ, который ему
    /// поручают (см. <see cref="IUserDirectory.CanSeeDivisionAsync"/>).
    /// </summary>
    AssigneeOutsideDivision,

    /// <summary>
    /// Правкой нельзя ПОНИЗИТЬ гриф документа — это рассекречивание, а не исправление опечатки.
    /// </summary>
    /// <remarks>
    /// ИНВАРИАНТ БЕЗОПАСНОСТИ (ТБ-020). Повышение грифа безопасно: документ становится доступен
    /// более узкому кругу, и правило «не выше своего допуска» его удержит в поле зрения автора правки.
    /// Понижение действует ровно наоборот — одним полем формы документ ДСП открывается всем, у кого
    /// допуск ниже, причём задним числом и без отдельного следа. Рассекречивание обязано быть
    /// самостоятельной процедурой со своим правом и своей записью в журнале; до её появления —
    /// запрет. В СКИД грифа не было вовсе, так что аналога этому правилу там нет.
    /// </remarks>
    ClassificationDowngradeNotAllowed,

    /// <summary>
    /// Правкой нельзя сменить ГРУППУ типа документа («Исполнение» ↔ «Хранение»).
    /// </summary>
    /// <remarks>
    /// ИСПРАВЛЕНИЕ ДЕФЕКТА СКИД, а не перенос. Там смена типа на «Хранение» просто обнуляла приоритет
    /// и инспектора, а НАЗНАЧЕНИЯ документа оставались в базе — висячие поручения у документа, который
    /// по своей группе поручений иметь не может: они не показывались в карточке, но продолжали
    /// участвовать в проверке сроков и уведомлениях. Обратный переход «Хранение» → «Исполнение» столь
    /// же неполон: документ получал группу исполнения без единого назначения. Смена группы — это смена
    /// СМЫСЛА документа; такой документ регистрируют заново.
    /// </remarks>
    TypeGroupChangeNotAllowed,
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

/// <summary>Итог создания документа: статус, идентификатор и данные для уведомлений при успехе.</summary>
public sealed record DocumentCreateResult(
    DocumentWriteStatus Status, int DocumentId = 0, CreatedDocumentNotice? Notice = null);

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
    /// Правит реквизиты зарегистрированного документа (§3.2). Назначения не затрагиваются.
    /// </summary>
    /// <remarks>
    /// Разграничение — тем же предикатом, что и чтение: невидимый документ неотличим от
    /// несуществующего (<see cref="DocumentWriteStatus.NotFound"/>), менять то, чего не видишь, нельзя.
    /// Дополнительно к этому действуют три правила, которых в СКИД не было:
    /// <list type="number">
    /// <item>гриф нельзя понизить (<see cref="DocumentWriteStatus.ClassificationDowngradeNotAllowed"/>)
    /// и нельзя поднять выше своего допуска (<see cref="DocumentWriteStatus.ClassificationOutsideClearance"/>) —
    /// иначе документ исчез бы из поля зрения того, кто его же и правит;</item>
    /// <item>подразделение-владелец можно сменить только на разрешённое субъекту
    /// (<see cref="DocumentWriteStatus.DivisionOutsideClearance"/>);</item>
    /// <item>группа типа неизменна (<see cref="DocumentWriteStatus.TypeGroupChangeNotAllowed"/>).</item>
    /// </list>
    /// Конкурентная правка (<c>xmin</c>) даёт <see cref="DocumentWriteStatus.Conflict"/>: вторая
    /// сохранённая форма не должна молча затирать первую.
    /// </remarks>
    Task<DocumentUpdateResult> UpdateAsync(
        DocumentEdit edit, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Страница реестра документов с фильтрами (§3.4), новые первыми. Разграничение — НА ЭТАПЕ ВЫБОРКИ
    /// (инвариант 3, ТБ-020/021): выдаются только документы с грифом не выше допуска субъекта
    /// <paramref name="access"/> и из разрешённых ему подразделений; fail-closed — пустой список
    /// разрешённых подразделений даёт пустую выдачу, а не «все».
    /// </summary>
    /// <remarks>
    /// Постраничность — СЕРВЕРНАЯ. Раньше отдавался весь список: на демонстрации это незаметно,
    /// а на реальном корпусе означает, что каждое открытие реестра тянет из БД в память тысячи строк
    /// вместе с их кратким содержанием. Общее число считается ДО среза — иначе навигация не знает,
    /// сколько страниц.
    /// </remarks>
    Task<DocumentPage> ListAsync(
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
    /// Удаляет сопутствующее вложение документа.
    /// </summary>
    /// <remarks>
    /// Удаляется ФИЗИЧЕСКИ, вместе с файлом на диске: вложение — это черновик или справочный
    /// материал, а не часть зарегистрированного документа, и «мягкое» удаление оставило бы
    /// в хранилище байты, которые никто уже не увидит и не почистит. ВЕРСИОНИРУЕМЫЕ файлы документа
    /// (§3.3) так удалять нельзя и здесь не удаляются — у них своя история.
    /// Файл на диске удаляется ПОСЛЕ успешной записи в БД: обратный порядок при сбое БД оставил бы
    /// строку, указывающую в пустоту.
    /// </remarks>
    Task<DocumentWriteStatus> DeleteAttachmentAsync(
        int attachmentId, AccessContext access, CancellationToken cancellationToken = default);

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
    /// Добавляет назначение к УЖЕ зарегистрированному документу (§4.1). Новое назначение всегда
    /// «Зарегистрировано», без контролёра, со СВОИМ сроком: «единый срок» действует только на
    /// назначения, созданные при регистрации, и на добавленное позже НЕ распространяется (перенос
    /// решения СКИД). Пишет историю (переход «ниоткуда») и пересчитывает агрегат документа.
    /// </summary>
    /// <remarks>
    /// ВНИМАНИЕ на последствие пересчёта, оно не дефект, а свойство §4.3: добавление назначения
    /// ОТКАТЫВАЕТ агрегированный статус назад. У документа, где все назначения были «Исполнено»
    /// (агрегат «Исполнен») или «Снято» («Снят»), после добавления одного «Зарегистрировано» агрегат
    /// станет «Зарегистрирован» — закрытый документ снова становится незакрытым.
    /// Недоступный документ неотличим от несуществующего — см. <see cref="WriteAccessRule"/>.
    /// </remarks>
    Task<AssignmentAddResult> AddAssignmentAsync(
        int documentId, AssignmentDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Переназначает исполнителя назначения (§4.7). Статус, срок и контролёр НЕ меняются — новый
    /// исполнитель наследует срок прежнего, счёт времени заново не начинается (перенос решения СКИД).
    /// Пишет запись в <c>AssignmentReassignment</c>, попадающую в ленту событий (§4.8).
    /// </summary>
    /// <remarks>
    /// Два ОСОЗНАННЫХ ужесточения против СКИД, оба — исправления их дефектов, найденных разбором
    /// исходника (этап 3.2): (1) переназначение запрещено для «Исполнено» и «Снято с контроля» —
    /// в СКИД оно проходило из ЛЮБОГО статуса, то есть можно было переписать исполнителя уже
    /// закрытого поручения; (2) назначение того же исполнителя не считается изменением — в СКИД оно
    /// писало в аудит запись с одинаковыми «было/стало» и слало человеку «вы назначены исполнителем».
    /// Недоступное назначение неотличимо от несуществующего — см. <see cref="WriteAccessRule"/>.
    /// </remarks>
    Task<AssignmentReassignResult> ReassignAssigneeAsync(
        int assignmentId, int newAssigneeUserId, string? reason, AccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Лента событий назначения (§4.8) — переходы статусов, продления сроков и смены исполнителя
    /// в ЕДИНОМ хронологическом порядке, старые первыми. <see langword="null"/> — назначения нет либо его документ недоступен.
    /// </summary>
    /// <remarks>
    /// Данные копились с этапа 3.1 (история переходов пишется на каждом переходе), но наружу не
    /// отдавались — в карточке ленты не было. Это чтение, поэтому фильтр допуска обязателен, как и
    /// у карточки: недоступное неотличимо от несуществующего (ТБ-020/021).
    /// </remarks>
    Task<IReadOnlyList<AssignmentTimelineEvent>?> GetAssignmentTimelineAsync(
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
/// укладываться в допуск создающего (<see cref="DocumentWriteStatus.ClassificationOutsideClearance"/> /
/// <see cref="DocumentWriteStatus.DivisionOutsideClearance"/>): нельзя создать документ выше
/// собственного допуска и тем самым «потерять» его из виду.
/// ВАЖНО: здесь ответ НАМЕРЕННО подробный, а не «не найдено». Скрывать нечего — субъект узнаёт лишь
/// границы СВОЕГО допуска, которые и так его собственные; чужие документы этим ответом не выдаются.
/// </remarks>
public static class WriteAccessRule;

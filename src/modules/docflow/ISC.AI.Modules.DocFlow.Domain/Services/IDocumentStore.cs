using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

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

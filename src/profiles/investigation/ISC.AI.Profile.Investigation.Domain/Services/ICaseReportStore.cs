using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Документ по бланку в списке дела (ТФ-ДДЛ-04).</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="CaseId">Дело.</param>
/// <param name="Kind">Вид.</param>
/// <param name="ReportDate">Дата документа.</param>
/// <param name="PersonId">Объект (фигурант дела).</param>
/// <param name="PersonName">Имя объекта для показа.</param>
/// <param name="State">Состояние на момент чтения (вычислено, ТФ-ДДЛ-05).</param>
/// <param name="CreatedAt">Когда создан (UTC).</param>
/// <param name="CreatedByUserId">Автор.</param>
/// <param name="CurrentRevision">Номер актуальной редакции.</param>
public sealed record CaseReportRow(
    int Id,
    int CaseId,
    CaseReportKind Kind,
    DateOnly ReportDate,
    int? PersonId,
    string? PersonName,
    CaseReportState State,
    DateTime CreatedAt,
    int? CreatedByUserId,
    int CurrentRevision);

/// <summary>Редакция в истории документа (без полей — они читаются отдельно).</summary>
/// <param name="Number">Номер редакции.</param>
/// <param name="AuthorUserId">Автор.</param>
/// <param name="CreatedAt">Когда (UTC).</param>
/// <param name="EditReason">Причина правки после окна.</param>
public sealed record CaseReportRevisionRow(int Number, int? AuthorUserId, DateTime CreatedAt, string? EditReason);

/// <summary>
/// Запрос на правку архивного документа — ТОЛЬКО метаданные (ТБ-079): ни полей бланка, ни имени объекта.
/// </summary>
/// <param name="Id">Идентификатор запроса.</param>
/// <param name="ReportId">Документ.</param>
/// <param name="ReportKind">Вид документа.</param>
/// <param name="ReportDate">Дата документа.</param>
/// <param name="CaseId">Дело.</param>
/// <param name="CaseNumber">Номер дела.</param>
/// <param name="RequestedByUserId">Кто просит.</param>
/// <param name="Reason">Причина.</param>
/// <param name="RequestedAt">Когда подан (UTC).</param>
/// <param name="Status">Статус.</param>
/// <param name="DecidedByUserId">Кто решил.</param>
/// <param name="DecidedAt">Когда решено (UTC).</param>
/// <param name="ExpiresAt">До какого момента действует разрешение (UTC).</param>
public sealed record CaseReportPermitRow(
    int Id,
    int ReportId,
    CaseReportKind ReportKind,
    DateOnly ReportDate,
    int CaseId,
    string CaseNumber,
    int RequestedByUserId,
    string Reason,
    DateTime RequestedAt,
    ReportEditPermitStatus Status,
    int? DecidedByUserId,
    DateTime? DecidedAt,
    DateTime? ExpiresAt);

/// <summary>Карточка документа: шапка, актуальные поля, история и запрос субъекта на правку.</summary>
/// <param name="Report">Шапка.</param>
/// <param name="CaseNumber">Номер дела.</param>
/// <param name="Classification">Гриф.</param>
/// <param name="EditableUntil">Конец окна редактирования (UTC).</param>
/// <param name="Content">Поля актуальной редакции.</param>
/// <param name="Revisions">История редакций, новые первыми.</param>
/// <param name="MyPermit">Последний запрос субъекта на правку этого документа, если есть.</param>
/// <param name="CanEditNow">Субъект может сохранить правку сейчас (окно открыто или действует его разрешение).</param>
public sealed record CaseReportDetails(
    CaseReportRow Report,
    string CaseNumber,
    short Classification,
    DateTime EditableUntil,
    CaseReportContent Content,
    IReadOnlyList<CaseReportRevisionRow> Revisions,
    CaseReportPermitRow? MyPermit,
    bool CanEditNow);

/// <summary>Черновик нового документа.</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="Kind">Вид.</param>
/// <param name="ReportDate">Дата документа.</param>
/// <param name="PersonId">Объект — фигурант этого дела (необязательно).</param>
/// <param name="Content">Поля бланка.</param>
public sealed record CaseReportDraft(int CaseId, CaseReportKind Kind, DateOnly ReportDate, int? PersonId, CaseReportContent Content);

/// <summary>Исход записи документа по бланку.</summary>
public enum CaseReportWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Документ, дело или запрос не найдены либо недоступны (ТБ-021).</summary>
    NotFound = 1,

    /// <summary>Объект — не фигурант этого дела.</summary>
    InvalidPerson = 2,

    /// <summary>Окно редактирования закрыто, действующего разрешения нет.</summary>
    WindowClosed = 3,

    /// <summary>Документ изменён другим сотрудником после открытия — нужно обновить.</summary>
    Stale = 4,

    /// <summary>Документ неактивен.</summary>
    Inactive = 5,

    /// <summary>Запрос не нужен: окно ещё открыто, или уже есть запрос/действующее разрешение.</summary>
    PermitNotNeeded = 6,

    /// <summary>Нельзя решать собственный запрос (правило двух лиц).</summary>
    SelfDecision = 7,

    /// <summary>Запрос уже решён.</summary>
    AlreadyDecided = 8,
}

/// <summary>
/// Сводки и справки по бланку (ТФ-ДДЛ-04/05, ТФ-АДМ-06, ADR-0031). Документ виден ровно тогда, когда видно его
/// дело по полной решётке (floor ядра → политика профиля → роль/владение); недоступное неотличимо от
/// отсутствующего (ТБ-021). Состояние «архивный» вычисляется при каждом чтении и записи из времени создания и
/// окна; редакции только добавляются.
/// </summary>
public interface ICaseReportStore
{
    /// <summary>Документы дела (новые даты первыми); <paramref name="text"/> — подстрока по полям. <see langword="null"/> — дело недоступно.</summary>
    Task<IReadOnlyList<CaseReportRow>?> ListByCaseAsync(int caseId, string? text, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Карточка документа; <see langword="null"/> — недоступен.</summary>
    Task<CaseReportDetails?> GetAsync(int reportId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Поля редакции <paramref name="number"/>; <see langword="null"/> — нет такой или документ недоступен.</summary>
    Task<CaseReportContent?> GetRevisionAsync(int reportId, int number, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Создать документ с первой редакцией.</summary>
    Task<(CaseReportWriteResult Result, int ReportId)> CreateAsync(CaseReportDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохранить поля новой редакцией. <paramref name="expectedRevision"/> — редакция, от которой шла правка:
    /// если документ уже изменён — <see cref="CaseReportWriteResult.Stale"/>. После окна — только по
    /// действующему разрешению субъекта; его причина пишется в редакцию.
    /// </summary>
    Task<CaseReportWriteResult> UpdateAsync(int reportId, int expectedRevision, CaseReportContent content, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Пометить документ активным/неактивным (удаления нет).</summary>
    Task<CaseReportWriteResult> SetActiveAsync(int reportId, bool active, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Подать запрос на правку архивного документа с причиной.</summary>
    Task<CaseReportWriteResult> RequestPermitAsync(int reportId, string reason, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Запросы на правку — только метаданные (ТБ-079), под floor'ом строки запроса. Право Администратора
    /// проверяет вызывающий.
    /// </summary>
    Task<IReadOnlyList<CaseReportPermitRow>> ListPermitsAsync(bool pendingOnly, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Решить запрос: разрешить (на срок из настроек) или отказать. Собственный запрос решать нельзя.</summary>
    Task<CaseReportWriteResult> DecidePermitAsync(int permitId, bool approve, AccessContext access, CancellationToken cancellationToken = default);
}

/// <summary>Документ к выгрузке в .docx (ТФ-ДДЛ-04, ТБ-033).</summary>
/// <param name="Kind">Вид.</param>
/// <param name="CaseNumber">Номер дела.</param>
/// <param name="ReportDate">Дата документа.</param>
/// <param name="ObjectName">Объект.</param>
/// <param name="Revision">Номер редакции.</param>
/// <param name="Content">Поля бланка.</param>
/// <param name="ClassificationMarking">Маркировка грифа.</param>
/// <param name="Executor">Исполнитель (кто выгрузил).</param>
public sealed record CaseReportExport(
    CaseReportKind Kind,
    string CaseNumber,
    DateOnly ReportDate,
    string? ObjectName,
    int Revision,
    CaseReportContent Content,
    string ClassificationMarking,
    string Executor);

/// <summary>Отрисовка документа по бланку в .docx с маркировкой грифа в теле и свойствах файла (ТБ-033).</summary>
public interface ICaseReportRenderer
{
    /// <summary>Байты файла .docx.</summary>
    byte[] RenderDocx(CaseReportExport report);
}

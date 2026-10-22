using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Вид записи журнала суточной активности (ТФ-ОТЧ-05).</summary>
public enum ActivityMaterial
{
    /// <summary>Носитель, привязанный к делу (вид — фото, видео, аудио — даёт каталог «Медиа»).</summary>
    Media = 1,

    /// <summary>Документ документооборота, прикреплённый к делу.</summary>
    Document = 2,

    /// <summary>Сводка ОН или справка ОУ (ТФ-ДДЛ-04).</summary>
    Report = 3,
}

/// <summary>Запись журнала суточной активности: что, когда и кем введено в дело.</summary>
/// <param name="OccurredAtUtc">Момент привязки носителя или документа либо создания сводки, UTC.</param>
/// <param name="Material">Вид записи.</param>
/// <param name="CaseId">Дело.</param>
/// <param name="CaseNumber">Номер дела.</param>
/// <param name="TaskNumber">№ задания инициатора («Задание по объекту»); <see langword="null"/> — нет.</param>
/// <param name="DivisionId">Подразделение дела.</param>
/// <param name="CaseClassification">Гриф дела.</param>
/// <param name="UserId">Кто привязал или создал.</param>
/// <param name="MediaAssetId">Носитель (для <see cref="ActivityMaterial.Media"/>).</param>
/// <param name="DocumentId">Документ (для <see cref="ActivityMaterial.Document"/>).</param>
/// <param name="ReportKind">Вид сводки (для <see cref="ActivityMaterial.Report"/>).</param>
/// <param name="ReportIsActive">Сводка не помечена неактивной.</param>
/// <param name="ReportClassification">Гриф сводки.</param>
/// <param name="PersonId">Фигурант сводки; <see langword="null"/> — сводка по делу целиком.</param>
public sealed record ActivityEntry(
    DateTime OccurredAtUtc,
    ActivityMaterial Material,
    int CaseId,
    string CaseNumber,
    string? TaskNumber,
    int DivisionId,
    short CaseClassification,
    int? UserId,
    int? MediaAssetId = null,
    int? DocumentId = null,
    CaseReportKind? ReportKind = null,
    bool ReportIsActive = true,
    short? ReportClassification = null,
    int? PersonId = null);

/// <summary>Записи журнала и подписи объектов к ним.</summary>
/// <param name="Entries">Записи по времени.</param>
/// <param name="CaseObjects">Объекты дела (фигуранты с ролью «объект»): псевдоним, а без него — ФИО, через запятую.</param>
/// <param name="Persons">Фигуранты сводок: псевдоним, а без него — ФИО.</param>
/// <param name="Truncated">Записей больше предела — показаны первые по времени.</param>
public sealed record ActivityEntries(
    IReadOnlyList<ActivityEntry> Entries,
    IReadOnlyDictionary<int, string> CaseObjects,
    IReadOnlyDictionary<int, string> Persons,
    bool Truncated);

/// <summary>Счёт карточек объектов одного подразделения-инициатора (ОР-6.0.1).</summary>
/// <param name="InitiatorUnitId">Инициатор из справочника; <see langword="null"/> — не указан.</param>
/// <param name="Total">Заведено карточек в периоде.</param>
/// <param name="InProgress">Из них в работе.</param>
/// <param name="Suspended">Приостановлено.</param>
/// <param name="Closed">Закрыто.</param>
public sealed record ObjectCardCount(int? InitiatorUnitId, int Total, int InProgress, int Suspended, int Closed);

/// <summary>Счёт карточек объектов по инициаторам и гриф учтённых карточек.</summary>
public sealed record ObjectCardCounts(IReadOnlyList<ObjectCardCount> Rows, short MaxClassification);

/// <summary>
/// Данные отчётных форм профиля (ТФ-ОТЧ-05, ОР-6.0.1, ADR-0061): журнал суточной активности и статистика карточек
/// объектов по подразделениям-инициаторам.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021/071): дела — ТЕМ ЖЕ правилом, что реестр (floor ядра, политика профиля, сужение по роли);
/// сводки — ещё и под собственной решёткой; фигуранты в подписях — только доступные. Видимость носителей и документов
/// решают их пакеты — это делает вызывающий.
/// </remarks>
public interface IActivityReportStore
{
    /// <summary>Материалы, введённые в доступные дела за период [<paramref name="fromUtc"/>; <paramref name="toUtc"/>).</summary>
    /// <param name="fromUtc">Начало, UTC.</param>
    /// <param name="toUtc">Конец (не включительно), UTC.</param>
    /// <param name="divisionIds">Только дела этих подразделений (группа ОН/ОУ/ОТМ); <see langword="null"/> — все.</param>
    /// <param name="limit">Предел числа записей.</param>
    /// <param name="access">Контекст доступа (обязателен).</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<ActivityEntries> ListActivityAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<int>? divisionIds, int limit, AccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>Карточки объектов («Задание по объекту»), заведённые в периоде, по инициаторам.</summary>
    Task<ObjectCardCounts> CountObjectCardsAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<int>? divisionIds, AccessContext access,
        CancellationToken cancellationToken = default);
}

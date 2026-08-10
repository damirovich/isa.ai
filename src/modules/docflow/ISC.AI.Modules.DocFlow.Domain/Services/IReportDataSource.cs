using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Вид отчёта (разд. 6 ТЗ СКИД) — определяет группировку, итоги и набор колонок.</summary>
/// <remarks>
/// В СКИД видов НЕ БЫЛО: там один плоский реестр из 9 колонок, а «шесть отчётов» разд. 6 ТЗ покрывались
/// комбинацией необязательных фильтров. Само ТЗ задаёт шесть ПАРАМЕТРОВ среза, не описывая ни колонок,
/// ни группировок. Здесь они разведены в явные виды: «в разрезе подразделений» без группировки не
/// бывает, а «по срокам» в СКИД фильтровало по дате РЕГИСТРАЦИИ, то есть не работало по существу.
/// </remarks>
public enum ReportKind
{
    /// <summary>По ответственному лицу: группировка по исполнителю назначения.</summary>
    ByAssignee = 1,

    /// <summary>По инспектору документа.</summary>
    ByInspector = 2,

    /// <summary>По подразделению — статусы назначений в разрезе подразделений.</summary>
    ByDivision = 3,

    /// <summary>По срокам исполнения: отбор и сортировка по СРОКУ, а не по дате регистрации.</summary>
    ByDeadline = 4,

    /// <summary>Только просроченные назначения.</summary>
    Overdue = 5,

    /// <summary>По типам документов.</summary>
    ByDocumentType = 6,
}

/// <summary>Формат выгрузки отчёта (разд. 6 ТЗ).</summary>
public enum ReportFormat
{
    /// <summary>Excel (.xlsx).</summary>
    Excel = 1,

    /// <summary>Word (.docx).</summary>
    Word = 2,

    /// <summary>PDF.</summary>
    Pdf = 3,
}

/// <summary>
/// Отбор строк отчёта. Период ОБЯЗАТЕЛЕН — в СКИД все фильтры были необязательны, и «пустой фильтр»
/// означал выгрузку всего корпуса разом.
/// </summary>
/// <param name="From">Начало периода включительно.</param>
/// <param name="To">Конец периода включительно.</param>
/// <param name="DivisionId">Подразделение назначения; <see langword="null"/> — все доступные.</param>
/// <param name="AssigneeUserId">Исполнитель; <see langword="null"/> — все.</param>
/// <param name="InspectorUserId">Инспектор документа; <see langword="null"/> — все.</param>
/// <param name="TypeId">Тип документа; <see langword="null"/> — все.</param>
/// <param name="Status">Статус назначения; <see langword="null"/> — все.</param>
public sealed record ReportFilter(
    DateOnly From,
    DateOnly To,
    int? DivisionId = null,
    int? AssigneeUserId = null,
    int? InspectorUserId = null,
    int? TypeId = null,
    AssignmentStatus? Status = null);

/// <summary>
/// Строка отчёта — гранула НАЗНАЧЕНИЕ (как в СКИД): документ с несколькими назначениями даёт
/// несколько строк, поэтому «документов» и «строк» — разные величины, и итоги считают их отдельно.
/// </summary>
public sealed record ReportRow(
    int DocumentId,
    int AssignmentId,
    string? RegNumber,
    DateOnly RegDate,
    string TypeName,
    string ShortContent,
    int DivisionId,
    int? AssigneeUserId,
    int? InspectorUserId,
    DateOnly? Deadline,
    AssignmentStatus Status,
    short Classification);

/// <summary>
/// Данные отчёта: строки, попавшие в отбор, и максимальный гриф среди них.
/// </summary>
/// <param name="MaxClassification">
/// Наибольший гриф среди выданных строк. Нужен дважды: для маркировки самого файла (ТБ-033 — сводка
/// по документам ДСП сама является ДСП) и для классификации записи журнала (ТБ-032).
/// </param>
public sealed record ReportData(IReadOnlyList<ReportRow> Rows, short MaxClassification);

/// <summary>
/// Источник данных отчётов (разд. 6 ТЗ СКИД, этап 5 Э4-35).
/// </summary>
/// <remarks>
/// ГЛАВНОЕ ТРЕБОВАНИЕ — РАЗГРАНИЧЕНИЕ В ЗАПРОСЕ. Отчёт это МАССОВАЯ выгрузка содержимого документов,
/// то есть самое опасное место для утечки грифа: одна забытая проверка выдаёт наружу весь корпус.
/// В СКИД разграничения здесь не было вовсе — только грубая проверка роли, а в коде стоял комментарий,
/// что построчная видимость к отчётам НЕ применяется (у них и понятия грифа не существовало).
/// Здесь отбор идёт тем же предикатом, что список документов: гриф ≤ допуска И подразделение
/// ∈ разрешённых (ТБ-020/021) И сужающая политика профиля (ADR-0014).
/// </remarks>
public interface IReportDataSource
{
    /// <summary>Строки отчёта в пределах допуска субъекта. Пустой результат — законный исход.</summary>
    Task<ReportData> QueryAsync(
        ReportKind kind, ReportFilter filter, AccessContext access, CancellationToken cancellationToken = default);
}

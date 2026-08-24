using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

// Контракты УЧЁТА НАРУШЕНИЙ (Э5-01, Приложение §4): реестр, карточка, классификатор, дашборд.
// Разрез «интерфейс отдельно, контракты по агрегату» — как NormRegistryContracts.cs.

/// <summary>Черновик нарушения (заносится человеком; ИИ здесь ничего не считает).</summary>
public sealed record ViolationDraft(
    int DivisionId,
    int CategoryId,
    ViolationSeverity Severity,
    DateOnly DetectedAt,
    RemediationStatus RemediationStatus,
    string? SourceDocRef,
    string? SourceAssignmentRef,
    string? ReferenceDocRef,
    string? Cause,
    string? Recommendation);

/// <summary>Отбор реестра нарушений.</summary>
public sealed record ViolationListFilter(
    int? DivisionId = null,
    int? CategoryId = null,
    ViolationSeverity? Severity = null,
    RemediationStatus? RemediationStatus = null,
    DateOnly? DetectedFrom = null,
    DateOnly? DetectedTo = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>Строка реестра нарушений (названия — словарями, не join-ами на каждую строку).</summary>
public sealed record ViolationListItem(
    int Id,
    int DivisionId,
    string DivisionName,
    int CategoryId,
    string CategoryName,
    string? CategoryParentName,
    ViolationSeverity Severity,
    DateOnly DetectedAt,
    RemediationStatus RemediationStatus,
    string? SourceDocRef,
    bool IsRecurring);

/// <summary>Страница реестра: строки + общее число до среза.</summary>
public sealed record ViolationPage(IReadOnlyList<ViolationListItem> Rows, int TotalCount);

/// <summary>Карточка нарушения (для правки).</summary>
public sealed record ViolationDetails(
    int Id,
    int DivisionId,
    int CategoryId,
    ViolationSeverity Severity,
    DateOnly DetectedAt,
    RemediationStatus RemediationStatus,
    string? SourceDocRef,
    string? SourceAssignmentRef,
    string? ReferenceDocRef,
    string? Cause,
    string? Recommendation);

/// <summary>Узел классификатора видов (2 уровня: сфера → вид).</summary>
public sealed record ViolationCategoryNode(
    int Id,
    string Name,
    int? ParentId,
    int ViolationCount);

/// <summary>Итог операции учёта.</summary>
public enum ViolationWriteResult
{
    /// <summary>Выполнено.</summary>
    Ok,

    /// <summary>Нарушение/вид/подразделение не найдены.</summary>
    NotFound,

    /// <summary>Классификатор двухуровневый: вид нельзя вкладывать в вид (только сфера → вид).</summary>
    CategoryNotLeaf,

    /// <summary>Вид с таким названием уже есть на этом уровне.</summary>
    DuplicateName,

    /// <summary>Сфера с нарушениями или видами не удаляется (переиспользуйте или переименуйте).</summary>
    InUse,
}

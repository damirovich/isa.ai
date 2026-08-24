using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

// Контракты АРХИВА ПРОВЕРОК (§5.2.4, ТФ-АРХ-01/02): история нарушений по подразделениям,
// сгруппированная по справкам-проверкам (слабая ссылка ReferenceDocRef, ТО-инф-06).

/// <summary>Отбор архива. Отбор по виду включает виды сферы (как в реестре нарушений).</summary>
public sealed record ArchiveFilter(
    int? DivisionId = null,
    int? CategoryId = null,
    ViolationSeverity? Severity = null,
    RemediationStatus? RemediationStatus = null,
    DateOnly? DetectedFrom = null,
    DateOnly? DetectedTo = null,
    string? RefSearch = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>Нарушение в карточке проверки (история — без длинных текстов причин).</summary>
public sealed record ArchiveViolationRow(
    int Id,
    string CategoryName,
    string? CategoryParentName,
    ViolationSeverity Severity,
    DateOnly DetectedAt,
    RemediationStatus RemediationStatus);

/// <summary>
/// Группа архива: одна справка-проверка в одном подразделении (либо нарушения ВНЕ проверок —
/// <see cref="ReferenceDocRef"/> = <see langword="null"/>) со сводкой и нарушениями.
/// </summary>
public sealed record ArchiveGroup(
    string? ReferenceDocRef,
    int DivisionId,
    string DivisionName,
    int ViolationCount,
    int OpenCount,
    DateOnly FirstDetectedAt,
    DateOnly LastDetectedAt,
    ViolationSeverity MaxSeverity,
    IReadOnlyList<ArchiveViolationRow> Violations);

/// <summary>Страница архива: группы + общее число групп до среза.</summary>
public sealed record ArchivePage(IReadOnlyList<ArchiveGroup> Groups, int TotalGroups);

/// <summary>
/// Метаданные справки-проверки из документооборота (разрешённая слабая ссылка). Статус и инспектор —
/// уже подписями: профильному экрану не нужны перечисления чужого модуля.
/// </summary>
public sealed record InspectionDocumentCard(
    int DocumentId,
    string RegNumber,
    DateOnly RegDate,
    string TypeName,
    string? InspectorName,
    string? StatusLabel);

/// <summary>Группа архива вместе с карточкой справки (если документ найден и ДОСТУПЕН субъекту).</summary>
public sealed record ArchiveGroupView(ArchiveGroup Group, InspectionDocumentCard? Document);

/// <summary>Итог запроса архива.</summary>
public sealed record ArchiveResult(IReadOnlyList<ArchiveGroupView> Groups, int TotalGroups);

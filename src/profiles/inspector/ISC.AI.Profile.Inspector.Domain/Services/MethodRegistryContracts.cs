using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

// Контракты РЕЕСТРА МЕТОДИК (§5.2.9, Ц-03): сохранение сформированных методических документов,
// список с отбором, карточка, правка и утверждение. Разрез — как у остальных агрегатов профиля.

/// <summary>Черновик сохранения методики (всё — из результата генерации + субъект).</summary>
public sealed record MethodDocumentDraft(
    string ArtifactKind,
    string InspectionType,
    string Scope,
    string Body,
    string? CitationsJson,
    bool AllCitationsConfirmed,
    short Classification,
    int? CreatedByUserId);

/// <summary>Отбор реестра методик.</summary>
public sealed record MethodListFilter(
    string? ArtifactKind = null,
    MethodDocumentStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20);

/// <summary>Строка реестра методик.</summary>
public sealed record MethodDocumentListItem(
    int Id,
    string ArtifactKind,
    string InspectionType,
    string Scope,
    MethodDocumentStatus Status,
    bool AllCitationsConfirmed,
    short Classification,
    DateTime CreatedAt,
    string? CreatedByName);

/// <summary>Страница реестра методик: строки + общее число до среза.</summary>
public sealed record MethodPage(IReadOnlyList<MethodDocumentListItem> Rows, int TotalCount);

/// <summary>Карточка методики.</summary>
public sealed record MethodDocumentDetails(
    int Id,
    string ArtifactKind,
    string InspectionType,
    string Scope,
    string Body,
    string? CitationsJson,
    bool AllCitationsConfirmed,
    short Classification,
    MethodDocumentStatus Status,
    DateTime CreatedAt,
    string? CreatedByName,
    string? ApprovedByName);

/// <summary>Итог операции реестра методик.</summary>
public enum MethodWriteResult
{
    /// <summary>Выполнено.</summary>
    Ok,

    /// <summary>Не найдена ЛИБО недоступна по допуску — для субъекта неотличимо (ТБ-020-стиль).</summary>
    NotFound,

    /// <summary>Операция только для черновика (удаление утверждённой запрещено).</summary>
    NotDraft,
}

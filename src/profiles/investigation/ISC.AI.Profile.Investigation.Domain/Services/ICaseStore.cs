using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Черновик дела (ТФ-ДЕЛ-01). Гриф и подразделение обязательны — без умолчаний (ТБ-024).</summary>
public sealed record CaseDraft(
    string Number,
    string Title,
    CaseKind Kind,
    DateOnly OpenedAt,
    int? InvestigatorUserId,
    int DivisionId,
    short Classification,
    string? Basis,
    int? CreatedByUserId);

/// <summary>Фильтр списка дел.</summary>
public sealed record CaseFilter(
    string? Text = null,
    CaseKind? Kind = null,
    CaseStatus? Status = null,
    int? DivisionId = null,
    int? InvestigatorUserId = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>Строка списка дел.</summary>
public sealed record CaseRow(
    int Id,
    string Number,
    string Title,
    CaseKind Kind,
    CaseStatus Status,
    DateOnly OpenedAt,
    int? InvestigatorUserId,
    int DivisionId,
    short Classification,
    int MediaCount,
    int PersonCount);

/// <summary>Страница дел.</summary>
public sealed record CasePage(IReadOnlyList<CaseRow> Rows, int TotalCount);

/// <summary>Карточка дела (ТФ-ДЕЛ-02).</summary>
public sealed record CaseDetails(
    int Id,
    string Number,
    string Title,
    CaseKind Kind,
    CaseStatus Status,
    DateOnly OpenedAt,
    int? InvestigatorUserId,
    int DivisionId,
    short Classification,
    string? Basis,
    DateTime? ClosedAt,
    DateTime CreatedAt,
    IReadOnlyList<CaseMediaLinkRow> Media,
    IReadOnlyList<SearchAuthorizationRow> Authorizations);

/// <summary>Привязанный носитель (идентификатор в схеме <c>media</c> — по значению).</summary>
public sealed record CaseMediaLinkRow(int MediaAssetId, string? Place, int? LinkedByUserId, DateTime LinkedAt);

/// <summary>Основание поиска (ТБ-071).</summary>
public sealed record SearchAuthorizationRow(
    int Id, int CaseId, AuthorizationKind Kind, string Reference, DateOnly IssuedAt, int? IssuedByUserId, DateOnly? ValidUntil, string? Notes);

/// <summary>Черновик основания.</summary>
public sealed record SearchAuthorizationDraft(
    int CaseId, AuthorizationKind Kind, string Reference, DateOnly IssuedAt, int? IssuedByUserId, DateOnly? ValidUntil, string? Notes);

/// <summary>Исход записи дела.</summary>
public enum CaseWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Дело не найдено или недоступно (неразличимо, ТБ-020).</summary>
    NotFound = 1,

    /// <summary>Номер дела уже занят в подразделении.</summary>
    DuplicateNumber = 2,

    /// <summary>Гриф/подразделение вне допуска субъекта.</summary>
    OutsideClearance = 3,
}

/// <summary>
/// Хранилище дел (ТФ-ДЕЛ-01..03). Каждый метод чтения применяет floor ядра и профильную политику
/// (следователь — свои дела, руководитель — дела подразделения) на стороне БД; запись проверяет, что
/// гриф/подразделение дела в пределах допуска субъекта (ТБ-024).
/// </summary>
public interface ICaseStore
{
    /// <summary>Список дел субъекта.</summary>
    Task<CasePage> ListAsync(CaseFilter filter, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Карточка дела, если доступна.</summary>
    Task<CaseDetails?> GetAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Создать дело.</summary>
    Task<(CaseWriteResult Result, int CaseId)> CreateAsync(CaseDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Изменить реквизиты (гриф/подразделение не меняются — иначе рассинхрон с производными).</summary>
    Task<CaseWriteResult> UpdateAsync(int caseId, string title, CaseKind kind, DateOnly openedAt, int? investigatorUserId, string? basis, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Сменить статус; закрытие ставит <c>ClosedAt</c> (регламент удаления шаблонов — ТФ-ДЕЛ-04, отдельно).</summary>
    Task<CaseWriteResult> SetStatusAsync(int caseId, CaseStatus status, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Добавить основание поиска.</summary>
    Task<(CaseWriteResult Result, int AuthorizationId)> AddAuthorizationAsync(SearchAuthorizationDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Привязать носитель (идемпотентно).</summary>
    Task<CaseWriteResult> LinkMediaAsync(int caseId, int mediaAssetId, string? place, int? linkedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Носители дел (для области поиска), без решётки — вызывающий уже проверил доступ к делам.</summary>
    Task<IReadOnlyCollection<int>> ListMediaAssetIdsAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default);

    /// <summary>Дело носителя; <see langword="null"/> — не привязан.</summary>
    Task<int?> FindCaseByMediaAsync(int mediaAssetId, CancellationToken cancellationToken = default);

    /// <summary>Идентификаторы всех доступных субъекту дел (область «все доступные дела», ТФ-ПЛ-05).</summary>
    Task<IReadOnlyList<int>> ListAccessibleIdsAsync(AccessContext access, CancellationToken cancellationToken = default);
}

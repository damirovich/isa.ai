using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Дело, как его видит пакет «Медиа»: непрозрачный идентификатор, подпись и режимные поля.</summary>
/// <param name="CaseId">Идентификатор дела в схеме профиля.</param>
/// <param name="Number">Номер дела (для подписи).</param>
/// <param name="Title">Краткое название (для подписи).</param>
/// <param name="Classification">Гриф дела — наследуется носителями и шаблонами (ТБ-070).</param>
/// <param name="DivisionId">Подразделение дела.</param>
public sealed record CaseScopeItem(int CaseId, string Number, string Title, short Classification, int DivisionId);

/// <summary>Фигурант дела для привязки кандидата (ТФ-ВЕР-03); модуль знает только идентификатор и подпись.</summary>
public sealed record CasePersonItem(int PersonId, string DisplayName);

/// <summary>Основание поиска в деле (ТБ-071): непрозрачный идентификатор профиля и реквизиты для аудита.</summary>
public sealed record CaseAuthorizationItem(int AuthorizationId, string Reference);

/// <summary>Подтверждённый кандидат (два «подтверждён» разных субъектов, ТБ-073) — материал для «появления» фигуранта (ТФ-ПЕР-02).</summary>
public sealed record ConfirmedAppearance(
    int CaseId,
    int PersonRef,
    int SessionId,
    int CandidateId,
    int FaceId,
    int AssetId,
    int? FrameIndex,
    long? FrameTimestampMs,
    double Similarity,
    short Classification,
    int DivisionId,
    int ExpertUserId,
    int VerifierUserId,
    DateTime ConfirmedAtUtc);

/// <summary>
/// ПОРТ ПРОФИЛЯ: область дел субъекта (ТБ-071, ТФ-ПЛ-05) и связь носителей/фигурантов с делами. Модуль
/// понятия «дело» не имеет — он оперирует непрозрачными идентификаторами; всё про доступность дел по
/// роли и владению знает только профиль. Без реализации приложение не стартует (ТС-013).
/// </summary>
/// <remarks>
/// Fail-closed: <see cref="GetCaseAsync"/> возвращает <see langword="null"/> и для несуществующего, и для
/// недоступного дела — снаружи они неотличимы (ТБ-020/021). Все методы с <see cref="AccessContext"/>
/// обязаны применять floor ядра поверх ролевых правил.
/// </remarks>
public interface ICaseScope
{
    /// <summary>Дела, доступные субъекту (для выбора области поиска и загрузки).</summary>
    Task<IReadOnlyList<CaseScopeItem>> ListAccessibleCasesAsync(AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Дело, если оно доступно субъекту; иначе <see langword="null"/>.</summary>
    Task<CaseScopeItem?> GetCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Идентификаторы носителей, привязанных к делам (область поиска для <c>FaceSearchQuery.AssetIds</c>).</summary>
    /// <summary>Основания поиска дела (поручения, постановления, ОРМ), если дело доступно; иначе пусто.</summary>
    Task<IReadOnlyList<CaseAuthorizationItem>> ListAuthorizationsAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<int>> GetAssetIdsAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default);

    /// <summary>Дело, к которому привязан носитель; <see langword="null"/> — не привязан.</summary>
    Task<int?> GetCaseIdForAssetAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>Привязать носитель к делу (слабая ссылка по значению в схеме профиля, ТО-инф-08).</summary>
    Task LinkAssetAsync(int caseId, int assetId, string? place, int? linkedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Фигуранты дела для привязки кандидата.</summary>
    Task<IReadOnlyList<CasePersonItem>> ListPersonsAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Зафиксировать подтверждённое появление фигуранта (ТФ-ВЕР-03 → ТФ-ПЕР-02).</summary>
    Task RecordAppearanceAsync(ConfirmedAppearance appearance, CancellationToken cancellationToken = default);
}

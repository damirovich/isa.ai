using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Реализация порта пакета «Медиа» <see cref="ICaseScope"/>: область дел субъекта (ТБ-071, ТФ-ПЛ-05)
/// и связь носителей/фигурантов с делами. Тонкая обёртка над <see cref="ICaseStore"/> и
/// <see cref="IPersonStore"/> — вся решётка (floor ядра, политика профиля, роль/владение) живёт там,
/// здесь только перевод контрактов профиля в непрозрачные для модуля записи.
/// </summary>
public sealed class CaseScope(ICaseStore cases, IPersonStore persons) : ICaseScope
{
    // Область «все доступные дела» для выбора: страница заведомо больше любого реального числа дел
    // одного субъекта; постранично модулю не нужно — он строит список выбора целиком.
    private const int AllCasesPageSize = 10_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseScopeItem>> ListAccessibleCasesAsync(AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var page = await cases.ListAsync(new CaseFilter(PageSize: AllCasesPageSize), access, cancellationToken);
        return page.Rows
            .Select(r => new CaseScopeItem(r.Id, r.Number, r.Title, r.Classification, r.DivisionId))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CaseScopeItem?> GetCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        // Fail-closed: недоступное и несуществующее дело — одинаково null (ТБ-020/021).
        var details = await cases.GetAsync(caseId, access, cancellationToken);
        return details is null
            ? null
            : new CaseScopeItem(details.Id, details.Number, details.Title, details.Classification, details.DivisionId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseAuthorizationItem>> ListAuthorizationsAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var details = await cases.GetAsync(caseId, access, cancellationToken);
        return details is null
            ? []
            : details.Authorizations.Select(a => new CaseAuthorizationItem(a.Id, a.Reference)).ToList();
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<int>> GetAssetIdsAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default) =>
        cases.ListMediaAssetIdsAsync(caseIds, cancellationToken);

    /// <inheritdoc />
    public Task<int?> GetCaseIdForAssetAsync(int assetId, CancellationToken cancellationToken = default) =>
        cases.FindCaseByMediaAsync(assetId, cancellationToken);

    /// <inheritdoc />
    public async Task LinkAssetAsync(int caseId, int assetId, string? place, int? linkedByUserId, CancellationToken cancellationToken = default)
    {
        var result = await cases.LinkMediaAsync(caseId, assetId, place, linkedByUserId, cancellationToken);
        if (result != CaseWriteResult.Ok)
        {
            throw new InvalidOperationException($"Привязать носитель {assetId} к делу {caseId} не удалось: {result}.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CasePersonItem>> ListPersonsAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var rows = await persons.ListByCaseAsync(caseId, access, cancellationToken);
        return rows.Select(p => new CasePersonItem(p.Id, p.DisplayName)).ToList();
    }

    /// <inheritdoc />
    public Task RecordAppearanceAsync(ConfirmedAppearance appearance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        // ТБ-073: два «подтверждён» РАЗНЫХ сотрудников. Само правило — TwoPersonRule модуля; здесь
        // страховка на границе: самоподтверждение в «появление» не превращается ни при каких условиях.
        if (appearance.ExpertUserId == appearance.VerifierUserId)
        {
            throw new InvalidOperationException(
                "Одно лицо не может быть экспертом и верификатором одного результата (ТБ-073).");
        }

        var draft = new AppearanceDraft(
            appearance.PersonRef,
            appearance.CaseId,
            appearance.AssetId,
            appearance.FaceId,
            appearance.FrameIndex,
            appearance.FrameTimestampMs,
            appearance.SessionId,
            appearance.CandidateId,
            appearance.Similarity,
            appearance.ConfirmedAtUtc,
            appearance.ExpertUserId,
            appearance.VerifierUserId,
            appearance.Classification,
            appearance.DivisionId);

        return persons.AddAppearanceAsync(draft, cancellationToken);
    }
}

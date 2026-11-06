using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Регламент удаления шаблонов дела по закрытию (ТФ-ДЕЛ-04, ТБ-074) для закрытия, пришедшего из другого офиса (ADR-0068 v0.4).
/// Повторяет шаги сценария «сменить статус» (<c>SetCaseStatusCommand</c>): носители дела → снятие шаблонов и вырезок → акт
/// закрытия.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-074, ADR-0024): удалять ли — решение эксплуатанта (<see cref="InvestigationRetentionOptions"/>), а не кода;
/// сбой снятия — исход «не исполнен» без акта: подтвердить надзору было бы нечем, и оператор узнаёт об этом сразу.
/// </remarks>
public sealed class CaseClosureRegulation(ICaseStore cases, IMediaPurger purger, InvestigationRetentionOptions retention) : ICaseClosureRegulation
{
    /// <inheritdoc />
    public async Task<CaseClosureRegulationResult> ExecuteAsync(
        int caseId, string reason, int? subjectId, CancellationToken cancellationToken = default)
    {
        if (!retention.PurgeTemplatesOnCaseClosure)
        {
            return new CaseClosureRegulationResult(CaseClosureRegulationOutcome.NotRequired);
        }

        var assetIds = await cases.ListMediaAssetIdsAsync([caseId], cancellationToken);
        TemplatePurgeResult purge;
        try
        {
            purge = await purger.PurgeTemplatesAsync(assetIds, reason, subjectId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Широко намеренно (как у сценария смены статуса): любая причина, по которой биометрия не снята, — исход
            // «не исполнен», а не тишина.
            return new CaseClosureRegulationResult(CaseClosureRegulationOutcome.Failed, Problem: exception.Message);
        }

        await cases.SaveClosureActAsync(
            new CaseClosureActDraft(caseId, subjectId, assetIds.Count, purge.AssetsAffected, purge.TemplatesRemoved, purge.CropsRemoved),
            cancellationToken);
        return new CaseClosureRegulationResult(CaseClosureRegulationOutcome.Executed, purge.TemplatesRemoved);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Suggestions;

/// <summary>Состояние сверки носителя с эталонами фигурантов одного дела (ТФ-ПЕР-09) — что сказать сотруднику.</summary>
public enum CaseSuggestionState
{
    /// <summary>Дело закрыто — сверка не проводится.</summary>
    Closed = 1,

    /// <summary>Нет действующего основания поиска — без основания система не сверяет (ТБ-071).</summary>
    NoBasis = 2,

    /// <summary>У фигурантов дела нет эталонов с лицом — сверять не с чем.</summary>
    NoReferences = 3,

    /// <summary>Всё готово, но носитель ещё не сверялся (загружен до появления эталона или основания).</summary>
    NotChecked = 4,

    /// <summary>Сверялся, но после этого у фигурантов появились новые эталоны.</summary>
    Outdated = 5,

    /// <summary>Сверен с текущими эталонами.</summary>
    Checked = 6,
}

/// <summary>Сверка носителя с фигурантами одного дела.</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="CaseNumber">Номер дела для подписи.</param>
/// <param name="State">Состояние.</param>
/// <param name="ReferenceCount">Сколько сейчас действующих эталонов у фигурантов дела.</param>
/// <param name="LastRun">Последняя сверка носителя по этому делу, если была.</param>
public sealed record CaseSuggestionStatus(
    int CaseId, string CaseNumber, CaseSuggestionState State, int ReferenceCount, SuggestionRunRow? LastRun);

/// <summary>Сверка носителя с фигурантами его дел — для карточки носителя (ТФ-ПЕР-09).</summary>
/// <param name="Enabled">Автоматическая сверка включена настройкой.</param>
/// <param name="CanRequest">Сотрудник вправе запустить «Сверить сейчас» и есть дело, готовое к сверке.</param>
/// <param name="MaxCosineDistance">Порог сверки (косинусное расстояние): сходство не ниже 1 − порог.</param>
/// <param name="Cases">Доступные субъекту дела носителя.</param>
/// <param name="Suggestions">Кандидаты «предложено системой» на лицах носителя.</param>
public sealed record AssetSuggestionStatus(
    bool Enabled,
    bool CanRequest,
    double MaxCosineDistance,
    IReadOnlyList<CaseSuggestionStatus> Cases,
    IReadOnlyList<SuggestedCandidateRow> Suggestions);

/// <summary>
/// Что система сделала с носителем по ТФ-ПЕР-09 и почему, если ничего: для блока «Сверка с фигурантами дела» на
/// карточке носителя. Без отдельной записи в журнал: блок — часть карточки, открытие которой уже записано (ТБ-030).
/// </summary>
/// <param name="AssetId">Носитель.</param>
public sealed record GetAssetSuggestionStatusQuery(int AssetId) : IRequest<ResponseDto<AssetSuggestionStatus>>
{
    /// <summary>Единый ответ «нет или нельзя» (ТБ-020/021).</summary>
    public const string NotFoundMessage = "Носитель не найден или недоступен.";

    /// <inheritdoc cref="GetAssetSuggestionStatusQuery" />
    public sealed class Handler(
        IAccessContextProvider accessProvider,
        ICaseScope caseScope,
        ISuggestionRunStore runs,
        ISearchSessionStore sessions,
        IMediaAdministration administration,
        MediaSearchOptions options)
        : IRequestHandler<GetAssetSuggestionStatusQuery, ResponseDto<AssetSuggestionStatus>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AssetSuggestionStatus>> Handle(
            GetAssetSuggestionStatusQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021, ТБ-071): чужой носитель неотличим от несуществующего.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (!await caseScope.IsAssetAccessibleAsync(query.AssetId, access, cancellationToken))
            {
                return ResponseDto<AssetSuggestionStatus>.NotFound(NotFoundMessage);
            }

            // Только дела носителя, доступные субъекту: про чужие дела носителя карточка не говорит ничего.
            var readiness = await caseScope.ListSuggestionReadinessAsync(query.AssetId, access, cancellationToken);
            var caseIds = readiness.Select(r => r.CaseId).ToArray();
            var lastRuns = (await runs.ListLatestAsync(query.AssetId, caseIds, access, cancellationToken))
                .ToDictionary(r => r.CaseId);
            var suggestions = await sessions.ListSuggestedForAssetAsync(query.AssetId, caseIds, access, cancellationToken);

            var cases = readiness
                .Select(r => new CaseSuggestionStatus(
                    r.CaseId, r.CaseNumber, StateOf(r, lastRuns.GetValueOrDefault(r.CaseId)), r.ReferenceCount,
                    lastRuns.GetValueOrDefault(r.CaseId)))
                .ToList();

            var canRequest = options.AutoSuggestEnabled
                && cases.Any(c => c.State is CaseSuggestionState.NotChecked or CaseSuggestionState.Outdated or CaseSuggestionState.Checked)
                && await administration.CanUploadAsync(cancellationToken);

            return ResponseDto<AssetSuggestionStatus>.Ok(new AssetSuggestionStatus(
                options.AutoSuggestEnabled, canRequest, options.EffectiveAutoSuggestMaxCosineDistance, cases, suggestions));
        }

        /// <summary>Состояние дела по правилу целей сверки в профиле: открыто, есть основание, есть эталоны; затем — давность сверки.</summary>
        public static CaseSuggestionState StateOf(SuggestionReadiness readiness, SuggestionRunRow? lastRun)
        {
            ArgumentNullException.ThrowIfNull(readiness);

            if (!readiness.IsOpen)
            {
                return CaseSuggestionState.Closed;
            }

            if (!readiness.HasBasis)
            {
                return CaseSuggestionState.NoBasis;
            }

            if (readiness.ReferenceCount == 0)
            {
                return CaseSuggestionState.NoReferences;
            }

            if (lastRun is null)
            {
                return CaseSuggestionState.NotChecked;
            }

            return readiness.LatestReferenceAtUtc is { } latest && latest > lastRun.CreatedAtUtc
                ? CaseSuggestionState.Outdated
                : CaseSuggestionState.Checked;
        }
    }
}

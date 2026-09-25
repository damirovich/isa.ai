using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Transcripts;

/// <summary>
/// Поиск по словам в расшифровках материалов дела (ADR-0026, предлагаемый ТФ-МЕД-09): найденные фрагменты с
/// таймкодами для перехода к месту записи, по носителю и времени. Совпадение — подстрока без учёта регистра
/// (у киргизского нет морфологического словаря: «үйдө», «үйгө» находятся по «үй»).
/// </summary>
/// <remarks>
/// РЕЖИМ. Область — только носители ОДНОГО дела, доступного субъекту (ТБ-071): недоступное дело неотличимо от
/// несуществующего (ТБ-020/021). Внутри области решётка гриф/подразделение применяется ещё раз на стороне БД —
/// по каждому фрагменту и носителю (каталог). Без контекста доступа — отказ (fail-closed, ТБ-021).
/// АУДИТ (ТБ-030): <see cref="IAuditableRequest"/> с <see cref="AuditAction.Search"/> — в журнал попадают дело и
/// ИСКОМЫЙ ТЕКСТ (он сам по себе раскрывает, что искали по делу, — поэтому в чувствительной части записи).
/// Выдача ограничена <see cref="MaxHits"/> фрагментами; больше — значит, запрос надо уточнить.
/// </remarks>
/// <param name="CaseId">Дело, по материалам которого ищут.</param>
/// <param name="Text">Искомые слова (подстрока, 2–200 символов после обрезки пробелов).</param>
public sealed record SearchTranscriptsQuery(int CaseId, string Text)
    : IRequest<ResponseDto<IReadOnlyList<TranscriptHit>>>, IAuditableRequest
{
    /// <summary>Предел выдачи: столько фрагментов показывается за один поиск.</summary>
    public const int MaxHits = 200;

    /// <summary>Минимальная длина искомого текста после обрезки пробелов.</summary>
    public const int MinTextLength = 2;

    /// <summary>Максимальная длина искомого текста после обрезки пробелов.</summary>
    public const int MaxTextLength = 200;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    /// <remarks>Текст — обрезанный и не длиннее <see cref="MaxTextLength"/>: запись журнала не раздувается мусорным вводом.</remarks>
    public string? AuditSummary =>
        "media:transcripts:search:case=" + CaseId.ToString(CultureInfo.InvariantCulture) + ";text=" + AuditText(Text);

    private static string AuditText(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return trimmed.Length > MaxTextLength ? trimmed[..MaxTextLength] : trimmed;
    }

    /// <inheritdoc cref="SearchTranscriptsQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, ICaseScope caseScope, IMediaCatalog catalog)
        : IRequestHandler<SearchTranscriptsQuery, ResponseDto<IReadOnlyList<TranscriptHit>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<TranscriptHit>>> Handle(
            SearchTranscriptsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска GetCurrentAsync бросает — ни поиска, ни выдачи.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // ТБ-071: поиск только в контексте доступного дела; недоступное неотличимо от несуществующего.
            var caseItem = await caseScope.GetCaseAsync(query.CaseId, access, cancellationToken);
            if (caseItem is null)
            {
                return ResponseDto<IReadOnlyList<TranscriptHit>>.NotFound("Дело не найдено или недоступно.");
            }

            // Область — носители этого дела; решётку поверх неё применит каталог на стороне БД.
            var assetIds = await caseScope.GetAssetIdsAsync([caseItem.CaseId], cancellationToken);
            if (assetIds.Count == 0)
            {
                return ResponseDto<IReadOnlyList<TranscriptHit>>.Ok([], 0);
            }

            var hits = await catalog.SearchTranscriptsAsync(
                assetIds, (query.Text ?? string.Empty).Trim(), MaxHits, access, cancellationToken);
            return ResponseDto<IReadOnlyList<TranscriptHit>>.Ok(hits, hits.Count);
        }
    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Журнал сверок носителей с эталонами фигурантов (ТФ-ПЕР-09, ADR-0035): чтобы карточка носителя честно говорила,
/// сверялся ли он и с каким итогом, а не догадывалась по отсутствию предложений. Аудит (ТБ-072) пишется отдельно —
/// этот журнал его не заменяет.
/// </summary>
public interface ISuggestionRunStore
{
    /// <summary>Записать итог сверки (фоновый конвейер, без субъекта).</summary>
    Task RecordAsync(SuggestionRunDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Последняя сверка носителя по каждому из дел <paramref name="caseIds"/> — под решёткой (ТБ-020/021); дела вне
    /// списка (вне области субъекта, ТБ-071) не читаются.
    /// </summary>
    Task<IReadOnlyList<SuggestionRunRow>> ListLatestAsync(
        int assetId, IReadOnlyCollection<int> caseIds, AccessContext access, CancellationToken cancellationToken = default);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Modules.Media.Application.Features.Verification;

/// <summary>
/// Сведения о материале кандидатов (ТФ-ПЛ-02): время съёмки, загрузки и источник — из каталога пакета, место — из
/// привязки к делу (порт профиля). Двумя запросами на страницу, а не на каждого кандидата.
/// </summary>
public interface IMaterialContextReader
{
    /// <summary>
    /// Сведения о носителях <paramref name="assetIds"/> под контекстом <paramref name="access"/>: носитель выше допуска
    /// в ответ не попадает (ТБ-020/021), место — только по делам субъекта (ТБ-071).
    /// </summary>
    Task<IReadOnlyDictionary<int, MaterialContext>> ReadAsync(
        IReadOnlyCollection<int> assetIds, AccessContext access, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IMaterialContextReader" />
public sealed class MaterialContextReader(IMediaCatalog catalog, ICaseScope caseScope) : IMaterialContextReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, MaterialContext>> ReadAsync(
        IReadOnlyCollection<int> assetIds, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var ids = assetIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<int, MaterialContext>();
        }

        var infos = await catalog.ListMaterialInfoAsync(ids, access, cancellationToken);
        if (infos.Count == 0)
        {
            return new Dictionary<int, MaterialContext>();
        }

        var places = await caseScope.ListAssetPlacesAsync(infos.Keys.ToArray(), access, cancellationToken);
        return infos.ToDictionary(
            kv => kv.Key,
            kv => new MaterialContext(
                kv.Value.CapturedAt, kv.Value.UploadedAtUtc, kv.Value.Source, places.TryGetValue(kv.Key, out var place) ? place : null));
    }
}

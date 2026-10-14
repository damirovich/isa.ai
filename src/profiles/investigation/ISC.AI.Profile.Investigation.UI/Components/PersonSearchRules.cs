using System.Globalization;
using ISC.AI.Modules.Media.Application.Features.Search;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>План поиска по фигуранту: куда перейти, либо почему нельзя.</summary>
/// <param name="Url">Адрес страницы поиска с заполненной формой; <see langword="null"/> — поиск невозможен.</param>
/// <param name="Blocker">Почему кнопка неактивна (показывается рядом с ней).</param>
/// <param name="Probe">Лучший эталон (первая проба).</param>
/// <param name="ProbeCount">Сколько эталонов идёт в поиск: больше одного — поиск по всем эталонам (ADR-0046).</param>
public sealed record PersonSearchPlan(string? Url, string? Blocker, ReferencePhotoRow? Probe, int ProbeCount = 0);

/// <summary>
/// Поиск по фигуранту одной кнопкой (ТФ-ПЛ-01/03/05, ТФ-ПЕР-03): пробы — все действующие эталоны с лицом (до
/// <see cref="SearchByFaceQuery.MaxProbeFaces"/>, лучшие по качеству; ADR-0046), область — все доступные дела,
/// основание — единственное основание дела (если их несколько — выбирает оператор на странице поиска). Один эталон —
/// прежний поиск по одному лицу. Правило чистое — проверяется тестом; права и аудит остаются за сценарием поиска
/// модуля «Медиа».
/// </summary>
public static class PersonSearchRules
{
    /// <summary>Лучшая проба: действующий эталон с лицом, не с отозванного появления; выше качество — раньше, при равенстве — новее.</summary>
    public static ReferencePhotoRow? BestProbe(IEnumerable<ReferencePhotoRow> photos, IEnumerable<AppearanceRow> appearances) =>
        Probes(photos, appearances) is { Count: > 0 } probes ? probes[0] : null;

    /// <summary>
    /// Пробы поиска по всем эталонам (ADR-0046): действующие эталоны с лицом, не с отозванного появления, без повторов
    /// лица; лучшие по качеству (при равенстве — новее), не больше <see cref="SearchByFaceQuery.MaxProbeFaces"/>.
    /// </summary>
    public static IReadOnlyList<ReferencePhotoRow> Probes(IEnumerable<ReferencePhotoRow> photos, IEnumerable<AppearanceRow> appearances)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(appearances);

        var list = appearances.ToList();
        var revokedOnly = list.Where(a => a.IsRevoked).Select(a => a.MediaFaceId)
            .Except(list.Where(a => !a.IsRevoked).Select(a => a.MediaFaceId))
            .ToHashSet();

        return photos
            .Where(p => p.SupersededById is null && p.MediaFaceId is { } face && !revokedOnly.Contains(face))
            .OrderByDescending(p => p.QualityScore ?? 0f)
            .ThenByDescending(p => p.CreatedAt)
            .DistinctBy(p => p.MediaFaceId)
            .Take(SearchByFaceQuery.MaxProbeFaces)
            .ToList();
    }

    /// <summary>План поиска для фигуранта дела <paramref name="caseId"/>.</summary>
    public static PersonSearchPlan Plan(
        int caseId,
        IEnumerable<ReferencePhotoRow> photos,
        IEnumerable<AppearanceRow> appearances,
        PersonSearchContext? context)
    {
        if (context is null)
        {
            return new PersonSearchPlan(null, "Проверяю права на поиск…", null);
        }

        if (!context.CanSearch)
        {
            return new PersonSearchPlan(null, "Поиск по лицу закрыт для вашей роли (матрица доступа)", null);
        }

        if (context.Authorizations.Count == 0)
        {
            return new PersonSearchPlan(null, "У дела нет основания поиска — добавьте его в карточке дела", null);
        }

        var probes = Probes(photos, appearances);
        if (probes.Count == 0 || probes[0] is not { MediaFaceId: { } faceId } probe)
        {
            return new PersonSearchPlan(null, "Нет действующего эталона с лицом — добавьте эталон на вкладке «Эталоны»", null);
        }

        // Несколько эталонов — поиск по всем сразу со сводкой кандидатов (ТФ-ПЕР-03, ADR-0046); один — по одному лицу.
        var inv = CultureInfo.InvariantCulture;
        var probeParameter = probes.Count > 1
            ? "faceIds=" + string.Join(",", probes.Select(p => p.MediaFaceId!.Value.ToString(inv)))
            : $"faceId={faceId.ToString(inv)}";
        var url = $"/media/search?caseId={caseId.ToString(inv)}&{probeParameter}&scope=all";

        // Одно основание — подставляем и запускаем сразу; несколько — выбирает оператор (основание фиксируется в аудите).
        if (context.Authorizations.Count == 1)
        {
            url += $"&authId={context.Authorizations[0].AuthorizationId.ToString(inv)}&auto=1";
        }

        return new PersonSearchPlan(url, null, probe, probes.Count);
    }
}

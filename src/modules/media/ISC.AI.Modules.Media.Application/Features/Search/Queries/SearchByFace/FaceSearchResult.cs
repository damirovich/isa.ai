using System.Collections.Generic;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Search;

/// <summary>
/// Итог поиска по лицу (ТФ-ПЛ-02): сессия, хеш пробы, кандидат-лист и параметры, с которыми он получен.
/// Кандидаты — только «кандидаты» с баллом схожести; никакого «совпадения»/«идентификации» (ТЭ-005..007).
/// </summary>
/// <param name="SessionId">Поисковая сессия (ТО-инф-12).</param>
/// <param name="ProbeSha256">SHA-256 пробы (изображения либо байтов шаблона), hex.</param>
/// <param name="ProbeCropStoredFileName">
/// Вырезка пробы-ИЗОБРАЖЕНИЯ (категория <c>media-probes</c>, подкаталог — дело сессии) для показа пары;
/// для пробы-лица носителя всегда <see langword="null"/> — вырезка принадлежит носителю и берётся по
/// <c>ProbeFaceId</c> (<c>GetFaceQuery</c>).
/// </param>
/// <param name="DetectedFaces">Сколько лиц найдено на пробном изображении (0 — проба задана шаблоном лица).</param>
/// <param name="Candidates">Кандидат-лист по рангу, как записан в сессии.</param>
/// <param name="DetectorVersion">Версия детектора.</param>
/// <param name="EmbedderVersion">Версия векторизатора.</param>
/// <param name="TopK">Действующая ширина кандидат-листа (после зажатия в границы ТН-008).</param>
/// <param name="MaxCosineDistance">Действующий порог расстояния (после предела ТФ-ПЛ-06); <see langword="null"/> — без порога.</param>
/// <param name="CaseIds">Дела, по носителям которых искали (область, ТФ-ПЛ-05).</param>
public sealed record FaceSearchResult(
    int SessionId,
    string ProbeSha256,
    string? ProbeCropStoredFileName,
    int DetectedFaces,
    IReadOnlyList<SearchCandidateRow> Candidates,
    string DetectorVersion,
    string EmbedderVersion,
    int TopK,
    double? MaxCosineDistance,
    IReadOnlyList<int> CaseIds);

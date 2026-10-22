using System.Collections.Generic;
using System.Linq;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порядок кадров лица для подсказки эталона (ТФ-МЕД-21, ADR-0058 п. 2): сначала пригодные (ТО-мат-07), среди них — по
/// оценке кадра; лица без оценок (проиндексированные раньше) — после оценённых, по прежнему общему баллу; затем по номеру
/// лица. Одно правило — для трека видео, для фото и для подсказки эталона фигуранта.
/// </summary>
public static class FaceRanking
{
    /// <summary>Кадры от лучшего к худшему.</summary>
    public static IReadOnlyList<FaceRow> Order(IEnumerable<FaceRow> faces) =>
        faces
            .OrderByDescending(f => f.QualityAcceptable)
            .ThenByDescending(f => f.Frame is not null)
            .ThenByDescending(f => f.Frame?.Score ?? 0f)
            .ThenByDescending(f => f.QualityScore)
            .ThenBy(f => f.Id)
            .ToList();

    /// <summary>Лучший кадр; <see langword="null"/> — кадров нет.</summary>
    public static FaceRow? Best(IEnumerable<FaceRow> faces) => Order(faces) is { Count: > 0 } ordered ? ordered[0] : null;
}

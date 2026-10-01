using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Indexing;

/// <summary>
/// Треки лиц видео (ТФ-ПЕР-02, ADR-0037): одно и то же лицо на соседних кадрах выборки получает общий номер трека —
/// по нему появление фигуранта показывается отрезком «с … по …» (первый и последний кадр трека), а не одним кадром.
/// </summary>
/// <remarks>
/// <para>
/// ЭТО НЕ ОПОЗНАНИЕ. Трек связывает лица только внутри одного видео и только на соседних кадрах (разрыв не больше
/// <c>maxGapMs</c>) — это непрерывность в кадре, а не вывод «это один человек вообще». Лица разных видео, а также
/// одного видео после долгого перерыва в треке не объединяются; подтверждение личности — по-прежнему двумя людьми (ТБ-073).
/// </para>
/// <para>
/// СОПОСТАВЛЕНИЕ. Лицо кадра присоединяется к живому треку: при шаблонах у обоих — по косинусной схожести не ниже
/// <c>minSimilarity</c>; если у одного из них шаблона нет (лицо непригодно для сравнения, ТО-мат-07) — по перекрытию
/// рамок (IoU) не ниже <see cref="MinOverlap"/>. Совпадение по шаблону всегда сильнее совпадения по рамке. Пары
/// разбираются жадно от лучшей: в одном кадре лицо и трек участвуют не больше одного раза. Неприсоединённое лицо
/// открывает новый трек. Детерминировано: одинаковый вход — одинаковые номера.
/// </para>
/// </remarks>
public static class FaceTracker
{
    /// <summary>Минимальное перекрытие рамок для лиц без шаблона (IoU).</summary>
    public const double MinOverlap = 0.3;

    /// <summary>
    /// Назначить номера треков лицам видео; порядок списка сохраняется. Лица без кадра (фото) возвращаются как есть,
    /// без трека.
    /// </summary>
    /// <param name="faces">Лица прогона в порядке обработки кадров.</param>
    /// <param name="minSimilarity">Порог косинусной схожести шаблонов для одного трека.</param>
    /// <param name="maxGapMs">Наибольший разрыв между кадрами одного трека, мс.</param>
    public static IReadOnlyList<IndexedFace> Assign(IReadOnlyList<IndexedFace> faces, double minSimilarity, long maxGapMs)
    {
        ArgumentNullException.ThrowIfNull(faces);

        var result = faces.ToArray();
        var tracks = new List<Track>();
        var frames = Enumerable.Range(0, result.Length)
            .Where(i => result[i].FrameTimestampMs is not null)
            .GroupBy(i => result[i].FrameTimestampMs!.Value)
            .OrderBy(g => g.Key);

        foreach (var frame in frames)
        {
            var timestampMs = frame.Key;
            var live = tracks.Where(t => t.LastMs < timestampMs && timestampMs - t.LastMs <= maxGapMs).ToList();

            // Все допустимые пары «лицо кадра × живой трек» — от лучшей к худшей; при равной оценке — трек, виденный
            // последним (непрерывность), затем номер трека и индекс лица — чтобы результат не зависел от порядка перебора.
            var pairs = frame
                .SelectMany(i => live.Select(t => (Face: i, Track: t, Score: Score(result[i], t, minSimilarity))))
                .Where(p => p.Score > 0)
                .OrderByDescending(p => p.Score)
                .ThenByDescending(p => p.Track.LastMs)
                .ThenBy(p => p.Track.Id)
                .ThenBy(p => p.Face)
                .ToList();

            var takenFaces = new HashSet<int>();
            var takenTracks = new HashSet<int>();
            foreach (var (face, track, _) in pairs)
            {
                if (takenFaces.Contains(face) || takenTracks.Contains(track.Id))
                {
                    continue;
                }

                takenFaces.Add(face);
                takenTracks.Add(track.Id);
                Extend(track, result[face], timestampMs);
                result[face] = result[face] with { TrackId = track.Id };
            }

            foreach (var face in frame.Where(i => !takenFaces.Contains(i)).Order())
            {
                var track = new Track(tracks.Count + 1);
                tracks.Add(track);
                Extend(track, result[face], timestampMs);
                result[face] = result[face] with { TrackId = track.Id };
            }
        }

        return result;
    }

    /// <summary>Оценка пары: по шаблону — 1 + схожесть (сильнее любой оценки по рамке), по рамке — IoU; 0 — не пара.</summary>
    private static double Score(IndexedFace face, Track track, double minSimilarity)
    {
        if (face.Template is { } template && track.Template is { } last)
        {
            var similarity = CosineSimilarity(template, last);
            return similarity >= minSimilarity ? 1 + similarity : 0;
        }

        var overlap = Overlap(face.Face.Box, track.Box);
        return overlap >= MinOverlap ? overlap : 0;
    }

    private static void Extend(Track track, IndexedFace face, long timestampMs)
    {
        track.LastMs = timestampMs;
        track.Box = face.Face.Box;
        if (face.Template is not null)
        {
            track.Template = face.Template;
        }
    }

    /// <summary>Косинусная схожесть; нулевой вектор — 0.</summary>
    private static double CosineSimilarity(float[] left, float[] right)
    {
        var length = Math.Min(left.Length, right.Length);
        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var i = 0; i < length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        return leftNorm > 0 && rightNorm > 0 ? dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm)) : 0;
    }

    /// <summary>Перекрытие рамок: площадь пересечения к площади объединения (IoU).</summary>
    private static double Overlap(BoundingBox left, BoundingBox right)
    {
        var width = Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X);
        var height = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y);
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var intersection = (double)width * height;
        var union = left.Area + right.Area - intersection;
        return union > 0 ? intersection / union : 0;
    }

    /// <summary>Живое состояние трека: последний кадр, рамка и последний известный шаблон.</summary>
    private sealed class Track(int id)
    {
        public int Id { get; } = id;

        public long LastMs { get; set; }

        public BoundingBox Box { get; set; }

        public float[]? Template { get; set; }
    }
}

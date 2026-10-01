using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Indexing;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Треки лиц видео (ТФ-ПЕР-02, ADR-0037): одно лицо на соседних кадрах — один трек; разрыв дольше предела, другой
/// человек или фото трек не продолжают; лицо без шаблона продолжает трек по перекрытию рамок; номера детерминированы.
/// </summary>
public sealed class FaceTrackerTests
{
    private const double MinSimilarity = MediaSearchOptions.DefaultTrackMinSimilarity;
    private const long MaxGapMs = 3000;

    private static readonly float[] PersonA = [1f, 0f, 0f];
    private static readonly float[] PersonANear = [0.95f, 0.3f, 0f];
    private static readonly float[] PersonB = [0f, 1f, 0f];

    [Fact(DisplayName = "Одно лицо на соседних кадрах — один трек; после разрыва дольше предела — новый трек")]
    public void Same_face_on_adjacent_frames_shares_track_until_gap()
    {
        var faces = new[]
        {
            Face(0, PersonA, x: 100),
            Face(1000, PersonANear, x: 110),
            Face(2000, PersonA, x: 120),
            Face(9000, PersonA, x: 120), // пропал из кадра на 7 с — это уже другой отрезок
        };

        Tracks(faces).ShouldBe([1, 1, 1, 2]);
    }

    [Fact(DisplayName = "Двое в кадре: треки по схожести лиц, а не по месту — поменялись местами, треки за ними")]
    public void Two_people_keep_their_tracks_by_similarity()
    {
        var faces = new[]
        {
            Face(0, PersonA, x: 100),
            Face(0, PersonB, x: 500),
            Face(1000, PersonB, x: 100), // B перешёл на место A
            Face(1000, PersonA, x: 500),
        };

        Tracks(faces).ShouldBe([1, 2, 2, 1]);
    }

    [Fact(DisplayName = "Другой человек на том же месте трек не продолжает; лицо без шаблона продолжает трек по перекрытию рамок")]
    public void Different_person_starts_new_track_and_unusable_face_joins_by_overlap()
    {
        var faces = new[]
        {
            Face(0, PersonA, x: 100),
            Face(1000, PersonB, x: 100),    // похожее место, но другое лицо — новый трек
            Face(2000, template: null, x: 105), // непригодное лицо: рамка почти там же, где у B на прошлом кадре
        };

        Tracks(faces).ShouldBe([1, 2, 2]);
    }

    [Fact(DisplayName = "Фото (без момента кадра) треков не получает; порядок лиц сохраняется; одинаковый вход — одинаковые номера")]
    public void Photos_untouched_and_numbering_is_deterministic()
    {
        var photo = new[] { Face(null, PersonA, x: 0), Face(null, PersonB, x: 300) };
        FaceTracker.Assign(photo, MinSimilarity, MaxGapMs).Select(f => f.TrackId).ShouldAllBe(t => t == null);

        var video = new[] { Face(0, PersonB, x: 300), Face(0, PersonA, x: 0), Face(1000, PersonA, x: 0) };
        var first = FaceTracker.Assign(video, MinSimilarity, MaxGapMs);
        var second = FaceTracker.Assign(video, MinSimilarity, MaxGapMs);

        first.Select(f => f.TrackId).ShouldBe(second.Select(f => f.TrackId));
        first.Select(f => f.Face.Box.X).ShouldBe(video.Select(f => f.Face.Box.X));
        first.Select(f => f.TrackId).ShouldBe(new int?[] { 1, 2, 2 });
    }

    [Fact(DisplayName = "Настройки трека: схожесть зажимается в 0..1, разрыв — только положительный, иначе умолчания")]
    public void Options_are_read_and_clamped()
    {
        var read = MediaSearchOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [MediaSearchOptions.TrackMinSimilarityKey] = "1.7",
            [MediaSearchOptions.TrackMaxGapSecondsKey] = "0",
        }).Build());
        read.TrackMinSimilarity.ShouldBe(1);
        read.TrackMaxGapSeconds.ShouldBe(MediaSearchOptions.DefaultTrackMaxGapSeconds);
        read.TrackMaxGapMs.ShouldBe(3000);

        new MediaSearchOptions(TrackMaxGapSeconds: 1.5).TrackMaxGapMs.ShouldBe(1500);
    }

    private static int?[] Tracks(IReadOnlyList<IndexedFace> faces) =>
        FaceTracker.Assign(faces, MinSimilarity, MaxGapMs).Select(f => f.TrackId).ToArray();

    private static IndexedFace Face(long? timestampMs, float[]? template, float x) => new(
        timestampMs is null ? null : (int)(timestampMs.Value / 1000),
        timestampMs,
        new DetectedFace(new BoundingBox(x, 100, 80, 80), default, 0.9f),
        new FaceQuality(0.9f, template is not null, template is null ? "мало" : null),
        template);
}

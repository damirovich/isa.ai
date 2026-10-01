using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Отрезок появления фигуранта в видео (ТФ-ПЕР-02, ADR-0037): список появлений получает начало и конец трека лица от
/// пакета «Медиа»; фото и видео без трека остаются как были; пакет спрашивается под тем же контекстом доступа.
/// </summary>
public sealed class AppearanceIntervalTests
{
    private static readonly AccessContext Access = new("7", 2, [1]);
    private static readonly int[] VideoFaces = [81, 82];

    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();

    public AppearanceIntervalTests()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
    }

    [Fact(DisplayName = "Появление в видео получает отрезок трека; фото и видео без трека — без отрезка; запрос к «Медиа» — только по видео")]
    public async Task Video_appearances_get_track_span()
    {
        _persons.ListAppearancesAsync(5, Access, Arg.Any<CancellationToken>()).Returns(
        [
            Row(1, faceId: 70, assetId: 27, timestampMs: null),   // фото
            Row(2, faceId: 81, assetId: 29, timestampMs: 15_000), // видео с треком
            Row(3, faceId: 82, assetId: 29, timestampMs: 40_000), // видео без трека (до переиндексации)
        ]);
        _catalog.GetTrackSpansAsync(Arg.Any<IReadOnlyCollection<FaceTrackRequest>>(), Access, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, FaceTrackSpan> { [81] = new(81, 4, 12_000, 47_000, 36) });

        var rows = (await new ListAppearancesQuery.Handler(_persons, _accessProvider, _catalog)
            .Handle(new ListAppearancesQuery(5), CancellationToken.None)).Data.ShouldNotBeNull();

        rows.Select(r => (r.Id, r.TrackStartMs, r.TrackEndMs, r.TrackFrames)).ShouldBe(
        [
            (1, (long?)null, (long?)null, (int?)null),
            (2, 12_000, 47_000, 36),
            (3, null, null, null),
        ]);
        await _catalog.Received(1).GetTrackSpansAsync(
            Arg.Is<IReadOnlyCollection<FaceTrackRequest>>(r => r.Select(x => x.FaceId).Order().SequenceEqual(VideoFaces)
                && r.All(x => x.AssetId == 29 && x.FrameTimestampMs != null)),
            Access, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Только фото — пакет «Медиа» про треки не спрашивается")]
    public async Task Photos_only_skip_track_query()
    {
        _persons.ListAppearancesAsync(5, Access, Arg.Any<CancellationToken>()).Returns([Row(1, 70, 27, null)]);

        (await new ListAppearancesQuery.Handler(_persons, _accessProvider, _catalog)
            .Handle(new ListAppearancesQuery(5), CancellationToken.None)).Data.ShouldNotBeNull().ShouldHaveSingleItem();

        await _catalog.DidNotReceiveWithAnyArgs().GetTrackSpansAsync(default!, default!, default);
    }

    private static AppearanceRow Row(int id, int faceId, int assetId, long? timestampMs) => new(
        id, PersonId: 5, CaseId: 7, MediaAssetId: assetId, MediaFaceId: faceId,
        FrameIndex: timestampMs is null ? null : (int)(timestampMs / 1000), FrameTimestampMs: timestampMs,
        SearchSessionId: 92, CandidateId: 945, Similarity: 0.65, Status: AppearanceStatus.InvestigativeLead,
        ConfirmedAtUtc: DateTime.UtcNow, ExpertUserId: 1, VerifierUserId: 2);
}

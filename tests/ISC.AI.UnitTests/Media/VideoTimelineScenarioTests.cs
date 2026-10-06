using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Отметки фигурантов на ленте видео (ADR-0038): карточка носителя получает подтверждённых фигурантов отрезками
/// треков их лиц; без трека — точкой на кадре появления; у фото не запрашиваются вовсе.
/// </summary>
public sealed class VideoTimelineScenarioTests
{
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly AccessContext _access = new("7", 2, [1]);

    public VideoTimelineScenarioTests()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(_access);
        _caseScope.IsAssetAccessibleAsync(5, _access, Arg.Any<CancellationToken>()).Returns(true);
        _catalog.ListFacesAsync(5, _access, Arg.Any<CancellationToken>()).Returns([]);
    }

    private static MediaAssetRow Asset(MediaKind kind) => new(
        5, kind, "v.mp4", "0123456789abcdef0123456789abcdef.mp4", "video/mp4", 1024, 70_000, null, null,
        0, 1, 1, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 0);

    private Task<MediaAssetDetails> LoadAsync(MediaKind kind)
    {
        _catalog.GetAsync(5, _access, Arg.Any<CancellationToken>()).Returns(Asset(kind));
        return Handle();
    }

    private async Task<MediaAssetDetails> Handle()
    {
        var handler = new GetMediaAssetQuery.Handler(_accessProvider, _catalog, _caseScope);
        var response = await handler.Handle(new GetMediaAssetQuery(5), CancellationToken.None);
        response.Status.ShouldBeTrue();
        return response.Data.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Видео: фигурант — отрезком трека лица; без трека — точкой на кадре; повтор на том же треке — одна отметка")]
    public async Task Video_person_spans_follow_tracks()
    {
        _caseScope.ListAssetAppearancesAsync(5, _access, Arg.Any<CancellationToken>()).Returns(
        [
            new AssetAppearanceItem(1, "Иванов И.И.", FaceId: 11, FrameTimestampMs: 12_000),
            new AssetAppearanceItem(1, "Иванов И.И.", FaceId: 12, FrameTimestampMs: 15_000),
            new AssetAppearanceItem(2, "Петров П.П.", FaceId: 30, FrameTimestampMs: 40_000),
        ]);
        _catalog.GetTrackSpansAsync(Arg.Any<IReadOnlyCollection<FaceTrackRequest>>(), _access, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, FaceTrackSpan>
            {
                [11] = new(11, 3, 10_000, 20_000, 11),
                [12] = new(12, 3, 10_000, 20_000, 11),
            });

        var details = await LoadAsync(MediaKind.Video);

        details.PersonSpans.ShouldNotBeNull().ShouldBe(
        [
            new AssetPersonSpan(1, "Иванов И.И.", 10_000, 20_000),
            new AssetPersonSpan(2, "Петров П.П.", 40_000, 40_000),
        ]);
        await _catalog.Received(1).GetTrackSpansAsync(
            Arg.Is<IReadOnlyCollection<FaceTrackRequest>>(r => r.Count == 3 && r.All(x => x.AssetId == 5)),
            _access, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Видео без подтверждённых фигурантов — треки не запрашиваются, отметок нет")]
    public async Task No_appearances_no_track_query()
    {
        _caseScope.ListAssetAppearancesAsync(5, _access, Arg.Any<CancellationToken>()).Returns([]);

        var details = await LoadAsync(MediaKind.Video);

        details.PersonSpans.ShouldNotBeNull().ShouldBeEmpty();
        await _catalog.DidNotReceiveWithAnyArgs().GetTrackSpansAsync(default!, default!, default);
    }

    [Fact(DisplayName = "Фото: появления для ленты не запрашиваются (ленты у фото нет)")]
    public async Task Image_does_not_ask_for_appearances()
    {
        var details = await LoadAsync(MediaKind.Image);

        details.PersonSpans.ShouldNotBeNull().ShouldBeEmpty();
        await _caseScope.DidNotReceiveWithAnyArgs().ListAssetAppearancesAsync(default, default!, default);
    }
}

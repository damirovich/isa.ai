using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Подсказка лучшего кадра для эталона (ТФ-МЕД-21, ADR-0058 п. 4) без БД: кадры — из подтверждённых и не отозванных
/// появлений на материалах дела фигуранта, у видео — весь трек появления (не соседние треки); действующие эталоны не
/// предлагаются, а служат для сравнения; материал чужого дела и недоступный носитель — не источник (ТБ-071).
/// </summary>
public sealed class ReferenceFrameSuggestionTests
{
    private const int PersonId = 7;
    private const int CaseId = 3;
    private static readonly AccessContext Access = new("42", 2, [5]);

    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public ReferenceFrameSuggestionTests()
    {
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _persons.GetAsync(PersonId, Access, Arg.Any<CancellationToken>())
            .Returns(new PersonRow(PersonId, CaseId, "Иванов Иван", false, null, null, null, 1, 5, 1, 3, PersonRole.Target));
        _caseScope.GetAssetIdsAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(CaseId)), Arg.Any<CancellationToken>())
            .Returns(new[] { 10, 11 });
        _caseScope.IsAssetAccessibleAsync(Arg.Any<int>(), Access, Arg.Any<CancellationToken>()).Returns(true);
        _persons.ListAppearancesAsync(PersonId, Access, Arg.Any<CancellationToken>()).Returns(
        [
            Appearance(1, assetId: 10, faceId: 100, AppearanceStatus.InvestigativeLead),
            Appearance(2, assetId: 11, faceId: 200, AppearanceStatus.Revoked),
            Appearance(3, assetId: 12, faceId: 300, AppearanceStatus.InvestigativeLead), // материал чужого дела
        ]);
        _catalog.ListFacesAsync(10, Access, Arg.Any<CancellationToken>()).Returns(
        [
            Face(100, 10, track: 1, score: 0.50f),
            Face(101, 10, track: 1, score: 0.80f),
            Face(102, 10, track: 1, score: 0.60f),
            Face(103, 10, track: 2, score: 0.99f), // другой трек — другой человек
            Face(104, 10, track: 1, score: 0.95f, acceptable: false), // непригодный кадр эталоном не предлагается
        ]);
        _catalog.ListFacesAsync(11, Access, Arg.Any<CancellationToken>()).Returns([Face(200, 11, track: null, score: 0.97f)]);
        _catalog.ListFacesAsync(12, Access, Arg.Any<CancellationToken>()).Returns([Face(300, 12, track: null, score: 0.98f)]);
        _catalog.GetAsync(Arg.Any<int>(), Access, Arg.Any<CancellationToken>())
            .Returns(call => new MediaAssetRow((int)call[0], MediaKind.Video, "рынок.mp4", "a.mp4", "video/mp4", 1, 1000, null, null,
                1, 5, 1, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 3));
        _persons.ListReferencePhotosAsync(PersonId, Access, Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact(DisplayName = "Без эталонов: лучший пригодный кадр трека появления; отозванные, чужие дела и соседние треки — не источник")]
    public async Task Best_track_frame_without_references()
    {
        var suggestion = (await Handle()).Data.ShouldNotBeNull();

        suggestion.Suggested.ShouldNotBeNull().FaceId.ShouldBe(101);
        suggestion.Suggested.AssetId.ShouldBe(10);
        suggestion.Suggested.AssetFileName.ShouldBe("рынок.mp4");
        suggestion.Suggested.Metrics.ShouldNotBeNull().Score.ShouldBe(0.80f);
        suggestion.Current.ShouldBeNull();
        suggestion.SuggestedIsBetter.ShouldBeTrue();
        suggestion.FramesConsidered.ShouldBe(4); // 100, 101, 102, 104 — весь трек 1
        await _catalog.DidNotReceive().ListFacesAsync(11, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _catalog.DidNotReceive().ListFacesAsync(12, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Действующий эталон не предлагается, а сравнивается: следующий кадр хуже — «не лучше»; заменённый эталон не в счёт")]
    public async Task References_are_compared_not_suggested()
    {
        _persons.ListReferencePhotosAsync(PersonId, Access, Arg.Any<CancellationToken>()).Returns(
        [
            Reference(1, faceId: 101, supersededBy: null),
            Reference(2, faceId: 100, supersededBy: 1), // заменён — снова может быть предложен, но он хуже 102
        ]);
        _catalog.GetFaceAsync(101, Access, Arg.Any<CancellationToken>()).Returns(Face(101, 10, track: 1, score: 0.80f));

        var suggestion = (await Handle()).Data.ShouldNotBeNull();

        suggestion.Suggested.ShouldNotBeNull().FaceId.ShouldBe(102);
        suggestion.Current.ShouldNotBeNull().FaceId.ShouldBe(101);
        suggestion.SuggestedIsBetter.ShouldBeFalse();
        await _catalog.DidNotReceive().GetFaceAsync(100, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Носитель дела недоступен субъекту — не источник; фигурант недоступен — «не найден»; журнал — номер фигуранта")]
    public async Task Inaccessible_sources_and_person()
    {
        _caseScope.IsAssetAccessibleAsync(10, Access, Arg.Any<CancellationToken>()).Returns(false);
        (await Handle()).Data.ShouldNotBeNull().Suggested.ShouldBeNull();

        _persons.GetAsync(PersonId, Access, Arg.Any<CancellationToken>()).Returns((PersonRow?)null);
        (await Handle()).StatusCode.ShouldBe(ResponseStatusCode.NotFound);

        new SuggestReferenceFrameQuery(PersonId).AuditSummary.ShouldBe("investigation:person:7:reference-frame:view");
        new SuggestReferenceFrameValidator().Validate(new SuggestReferenceFrameQuery(0)).IsValid.ShouldBeFalse();
    }

    private async Task<ResponseDto<ReferenceFrameSuggestion>> Handle() =>
        await new SuggestReferenceFrameQuery.Handler(_persons, _caseScope, _catalog, _access)
            .Handle(new SuggestReferenceFrameQuery(PersonId), CancellationToken.None);

    private static AppearanceRow Appearance(int id, int assetId, int faceId, AppearanceStatus status) =>
        new(id, PersonId, CaseId, assetId, faceId, 1, 1_000, 500, 900, 0.8, status, DateTime.UtcNow, 42, null);

    private static FaceRow Face(int id, int assetId, int? track, float score, bool acceptable = true) =>
        new(id, assetId, 1, 1_000, 0, 0, 50, 50, 0.9f, 0.9f, acceptable, null, $"crop{id}.jpg", track, 1, 5,
            new FrameMetrics(0.5f, 0.5f, 0, 0, 0, 60, score));

    private static ReferencePhotoRow Reference(int id, int faceId, int? supersededBy) =>
        new(id, PersonId, 10, faceId, 0.9f, "фото", null, null, 42, supersededBy, DateTime.UtcNow);
}

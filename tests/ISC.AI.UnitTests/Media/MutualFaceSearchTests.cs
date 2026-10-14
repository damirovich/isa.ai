using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Scope;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Взаимный поиск ОН+ОУ (ADR-0044): материал кандидата из чужого дела открывается на просмотр, только если поиск шёл в
/// доступном субъекту деле; карточка носителя при этом — «только просмотр», без дела; без основания — «не найден».
/// </summary>
public sealed class MutualFaceSearchTests
{
    private const int AssetId = 50;
    private static readonly AccessContext Access = new("10", 2, [1]);

    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly ISearchSessionStore _sessions = Substitute.For<ISearchSessionStore>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();

    public MutualFaceSearchTests()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _sessions.ListCaseIdsWithCandidateAssetAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _catalog.GetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(Asset());
        _catalog.ListFacesAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns([Face()]);
        _catalog.GetFaceAsync(5, Access, Arg.Any<CancellationToken>()).Returns(Face());
    }

    [Fact(DisplayName = "ADR-0044: носитель своего дела — основание «дело», сессии поиска не спрашиваются")]
    public async Task Own_case_asset_is_viewable_by_case()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(true);

        (await AssetViewAccess.ResolveAsync(_caseScope, _sessions, AssetId, Access)).ShouldBe(AssetViewGrant.Case);

        await _sessions.DidNotReceiveWithAnyArgs().ListCaseIdsWithCandidateAssetAsync(default, default!, default);
    }

    [Fact(DisplayName = "ADR-0044: кандидат поиска в доступном деле — просмотр; кандидат чужой сессии по чужому делу — нет")]
    public async Task Candidate_is_viewable_only_through_accessible_search_case()
    {
        _sessions.ListCaseIdsWithCandidateAssetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns([8, 3]);

        // Сессия по делу 8 субъекту недоступна (чужое дело), по делу 3 — доступна.
        (await AssetViewAccess.ResolveAsync(_caseScope, _sessions, AssetId, Access)).ShouldBe(AssetViewGrant.None);

        _caseScope.GetCaseAsync(3, Access, Arg.Any<CancellationToken>()).Returns(new CaseScopeItem(3, "ОН-1", "Своё дело", 2, 1));
        (await AssetViewAccess.ResolveAsync(_caseScope, _sessions, AssetId, Access)).ShouldBe(AssetViewGrant.SearchCandidate);
        (await AssetViewAccess.CanViewAsync(_caseScope, _sessions, AssetId, Access)).ShouldBeTrue();
    }

    [Fact(DisplayName = "ADR-0044: карточка носителя-кандидата — «только просмотр» и без дела; без основания — «не найден»")]
    public async Task Asset_card_of_candidate_is_view_only()
    {
        var handler = new GetMediaAssetQuery.Handler(_accessProvider, _catalog, _caseScope, _sessions);

        var denied = await handler.Handle(new GetMediaAssetQuery(AssetId), CancellationToken.None);
        denied.StatusCode.ShouldBe(ResponseStatusCode.NotFound);

        GrantThroughSearchInCase(3);
        var response = await handler.Handle(new GetMediaAssetQuery(AssetId), CancellationToken.None);

        response.Status.ShouldBeTrue();
        var details = response.Data.ShouldNotBeNull();
        details.ViewOnly.ShouldBeTrue();
        details.CaseId.ShouldBeNull();
        details.Faces.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "ADR-0044: своя карточка носителя — не «только просмотр»")]
    public async Task Own_asset_card_is_not_view_only()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(true);
        _caseScope.GetCaseIdForAssetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(3);

        var response = await new GetMediaAssetQuery.Handler(_accessProvider, _catalog, _caseScope, _sessions)
            .Handle(new GetMediaAssetQuery(AssetId), CancellationToken.None);

        response.Data.ShouldNotBeNull().ViewOnly.ShouldBeFalse();
        response.Data.CaseId.ShouldBe(3);
    }

    [Fact(DisplayName = "ADR-0044: лицо кандидата из чужого дела открывается; без основания — «не найдено»")]
    public async Task Face_of_candidate_is_viewable()
    {
        var handler = new GetFaceQuery.Handler(_accessProvider, _catalog, _caseScope, _sessions);

        (await handler.Handle(new GetFaceQuery(5), CancellationToken.None)).StatusCode.ShouldBe(ResponseStatusCode.NotFound);

        GrantThroughSearchInCase(3);
        (await handler.Handle(new GetFaceQuery(5), CancellationToken.None)).Status.ShouldBeTrue();
    }

    private void GrantThroughSearchInCase(int caseId)
    {
        _sessions.ListCaseIdsWithCandidateAssetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns([caseId]);
        _caseScope.GetCaseAsync(caseId, Access, Arg.Any<CancellationToken>()).Returns(new CaseScopeItem(caseId, "ОН-1", "Своё дело", 2, 1));
    }

    private static MediaAssetRow Asset() => new(
        AssetId, MediaKind.Image, "photo.jpg", "0123456789abcdef0123456789abcdef.jpg", "image/jpeg", 1024, null, null, null,
        0, 2, 1, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 1);

    private static FaceRow Face() =>
        new(5, AssetId, null, null, 0, 0, 10, 10, 0.9f, 0.8f, true, null, "c.jpg", null, 2, 1);
}

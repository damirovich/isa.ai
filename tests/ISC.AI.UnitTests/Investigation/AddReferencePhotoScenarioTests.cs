using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Эталон фигуранта (ТФ-ПЕР-01, ТБ-077): носитель и лицо пакета «Медиа» принимаются ТОЛЬКО если носитель
/// доступен субъекту и привязан к делу фигуранта, а лицо — с этого носителя (ТБ-071, ТФ-ДЕЛ-03).
/// Идентификаторы перебираемы, поэтому любой отказ неразличим с «нет такого» (ТБ-020/021) и хранилище
/// при отказе не вызывается.
/// </summary>
public sealed class AddReferencePhotoScenarioTests
{
    private const int UserId = 42;
    private const int PersonId = 7;
    private const int CaseId = 3;
    private const int AssetId = 50;
    private const int FaceId = 5;

    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    private static readonly PersonRow Person = new(
        PersonId, CaseId, "Иванов", IsUnidentified: false, UnidentifiedNumber: null, RoleInCase: null, Notes: null,
        Classification: 1, DivisionId: 5, ReferencePhotoCount: 0, AppearanceCount: 0);

    public AddReferencePhotoScenarioTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)UserId);
        _roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext(UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), MaxClassification: 2, AllowedDivisions: [5]));

        // Благополучный сценарий по умолчанию: фигурант доступен, носитель доступен и в деле фигуранта,
        // лицо — с этого носителя, хранилище принимает.
        _persons.GetAsync(PersonId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Person);
        _caseScope.IsAssetAccessibleAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(true);
        _caseScope.GetAssetIdsAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.Contains(CaseId)), Arg.Any<CancellationToken>())
            .Returns([AssetId, 51]);
        _catalog.GetFaceAsync(FaceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Face(FaceId, AssetId));
        _persons.AddReferencePhotoAsync(Arg.Any<ReferencePhotoDraft>(), Arg.Any<int?>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.Ok, 99));
    }

    [Fact(DisplayName = "Носитель из дела фигуранта и лицо с него — эталон добавлен, в черновике идентификаторы и субъект")]
    public async Task Asset_of_person_case_with_its_face_is_accepted()
    {
        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId, Source: " паспорт ", LegalBasis: null));

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(99);
        await _persons.Received(1).AddReferencePhotoAsync(
            Arg.Is<ReferencePhotoDraft>(d =>
                d.PersonId == PersonId && d.MediaAssetId == AssetId && d.MediaFaceId == FaceId
                && d.Source == "паспорт" && d.AddedByUserId == UserId),
            null, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Без лица: каталог лиц не опрашивается, эталон по носителю добавлен")]
    public async Task Without_face_catalog_is_not_consulted()
    {
        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId));

        response.Status.ShouldBeTrue();
        await _catalog.DidNotReceive().GetFaceAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Носитель вне допуска/роли (IsAssetAccessible=false) → NotFound, хранилище не вызывается (ТБ-071)")]
    public async Task Inaccessible_asset_is_not_found()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(false);

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId));

        ShouldBeReferenceNotFound(response);
        await AssertStoreUntouchedAsync();
    }

    [Fact(DisplayName = "Носитель доступен, но привязан к ДРУГОМУ делу (не делу фигуранта) → NotFound (ТФ-ДЕЛ-03)")]
    public async Task Asset_of_another_case_is_not_found()
    {
        _caseScope.GetAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([51, 52]);

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId));

        ShouldBeReferenceNotFound(response);
        await AssertStoreUntouchedAsync();
        // Область запрашивалась именно для дела фигуранта.
        await _caseScope.Received(1).GetAssetIdsAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Count == 1 && ids.Contains(CaseId)), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Лицо с другого носителя → NotFound: «носитель A, лицо с B» не допускается")]
    public async Task Face_of_another_asset_is_not_found()
    {
        _catalog.GetFaceAsync(FaceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Face(FaceId, 51));

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId));

        ShouldBeReferenceNotFound(response);
        await AssertStoreUntouchedAsync();
    }

    [Fact(DisplayName = "Лицо не найдено/недоступно (каталог вернул null) → NotFound")]
    public async Task Unknown_face_is_not_found()
    {
        _catalog.GetFaceAsync(FaceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns((FaceRow?)null);

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId));

        ShouldBeReferenceNotFound(response);
        await AssertStoreUntouchedAsync();
    }

    [Fact(DisplayName = "Фигурант недоступен → NotFound, порты «Медиа» не опрашиваются (ТБ-020/021)")]
    public async Task Inaccessible_person_is_not_found_before_media_ports()
    {
        _persons.GetAsync(PersonId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns((PersonRow?)null);

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId));

        ShouldBeReferenceNotFound(response);
        await AssertStoreUntouchedAsync();
        await _caseScope.DidNotReceive().IsAssetAccessibleAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _catalog.DidNotReceive().GetFaceAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Эксперт, Верификатор и субъект без роли эталон не добавляют — ни хранилище, ни порты не вызываются (ТП-004)")]
    [InlineData(InvestigationRole.FaceExpert)]
    [InlineData(InvestigationRole.Verifier)]
    [InlineData(null)]
    public async Task Non_editors_are_denied(InvestigationRole? role)
    {
        _roles.GetRoleAsync(UserId, Arg.Any<CancellationToken>()).Returns(role);

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId));

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(RoleGuard.CaseDenied);
        await AssertStoreUntouchedAsync();
        await _persons.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().IsAssetAccessibleAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Отказ хранилища (NotFound) — тот же неразличимый ответ")]
    public async Task Store_not_found_maps_to_reference_not_found()
    {
        _persons.AddReferencePhotoAsync(Arg.Any<ReferencePhotoDraft>(), Arg.Any<int?>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns((PersonWriteResult.NotFound, 0));

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId));

        ShouldBeReferenceNotFound(response);
    }

    private async Task<ResponseDto<int>> HandleAsync(AddReferencePhotoCommand command) =>
        await new AddReferencePhotoCommand.Handler(_persons, _roles, _caseScope, _catalog, _subject, _access)
            .Handle(command, CancellationToken.None);

    private static void ShouldBeReferenceNotFound(ResponseDto<int> response)
    {
        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        response.StatusMessage.ShouldBe(PersonGuard.ReferenceNotFound);
    }

    private async Task AssertStoreUntouchedAsync() =>
        await _persons.DidNotReceive().AddReferencePhotoAsync(
            Arg.Any<ReferencePhotoDraft>(), Arg.Any<int?>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());

    private static FaceRow Face(int id, int assetId) => new(
        id, assetId, FrameIndex: null, FrameTimestampMs: null,
        BoxX: 0.1f, BoxY: 0.1f, BoxWidth: 0.2f, BoxHeight: 0.2f,
        DetectionScore: 0.9f, QualityScore: 0.8f, QualityAcceptable: true, QualityReason: null,
        CropStoredFileName: "c.jpg", TrackId: null, Classification: 1, DivisionId: 5);
}

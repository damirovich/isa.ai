using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
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
/// при отказе не вызывается. Вид носителя — только изображение или видео (ADR-0026): аудиозапись
/// отвергается отдельным ответом уже после проверок доступа.
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
        _catalog.GetAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Asset(AssetId, MediaKind.Image));
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
        await _catalog.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _catalog.DidNotReceive().GetFaceAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Аудиозапись не может быть источником эталона")]
    public async Task RejectsAudioAssetAsReferenceSource()
    {
        // Голосовое сообщение ИЗ ДЕЛА фигуранта и доступное субъекту: проверки доступа и принадлежности
        // пройдены, отказ — именно по виду носителя (ТБ-077, ADR-0026). Лицо не указано — как в сценарии
        // находки, где поле лица оставлено пустым.
        _catalog.GetAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Asset(AssetId, MediaKind.Audio));

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId));

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(AddReferencePhotoCommand.SourceKindDenied);
        await AssertStoreUntouchedAsync();
        // Носитель читался под контекстом доступа субъекта — решётка каталога не обойдена (ТБ-020/021).
        await _catalog.Received(1).GetAsync(
            AssetId, Arg.Is<AccessContext>(a => a.MaxClassification == 2), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Вид носителя, которого нет в списке разрешённых, отвергается: разрешение списком, а не запретом аудио")]
    public async Task Unknown_asset_kind_is_rejected_by_allow_list()
    {
        _catalog.GetAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Asset(AssetId, (MediaKind)99));

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(AddReferencePhotoCommand.SourceKindDenied);
        await AssertStoreUntouchedAsync();
    }

    [Fact(DisplayName = "Видеозапись дела — допустимый источник эталона (лицо с кадра)")]
    public async Task Video_asset_is_accepted_as_reference_source()
    {
        _catalog.GetAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Asset(AssetId, MediaKind.Video));

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId, FaceId));

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(99);
    }

    [Fact(DisplayName = "Каталог «Медиа» носитель не отдал (недоступен по решётке) → тот же NotFound, хранилище не вызывается")]
    public async Task Asset_missing_in_catalog_is_not_found()
    {
        _catalog.GetAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns((MediaAssetRow?)null);

        var response = await HandleAsync(new AddReferencePhotoCommand(PersonId, AssetId));

        ShouldBeReferenceNotFound(response);
        await AssertStoreUntouchedAsync();
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

    private static MediaAssetRow Asset(int id, MediaKind kind) => new(
        id, kind, "m.bin", "0123456789abcdef0123456789abcdef.bin", "application/octet-stream", 1024, DurationMs: null,
        Source: null, CapturedAt: null, Classification: 1, DivisionId: 5, UploadedByUserId: UserId,
        IndexStatus: kind == MediaKind.Audio ? MediaIndexStatus.NotApplicable : MediaIndexStatus.Indexed,
        IndexError: null, DetectorVersion: null, EmbedderVersion: null, IndexedAt: null,
        CreatedAt: new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc), FaceCount: 0);

    private static FaceRow Face(int id, int assetId) => new(
        id, assetId, FrameIndex: null, FrameTimestampMs: null,
        BoxX: 0.1f, BoxY: 0.1f, BoxWidth: 0.2f, BoxHeight: 0.2f,
        DetectionScore: 0.9f, QualityScore: 0.8f, QualityAcceptable: true, QualityReason: null,
        CropStoredFileName: "c.jpg", TrackId: null, Classification: 1, DivisionId: 5);
}

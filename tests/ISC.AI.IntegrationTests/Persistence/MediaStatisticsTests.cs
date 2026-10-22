using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Счётчики результативности распознавания (ТФ-ОТЧ-07, ADR-0061) на настоящем Postgres: сходятся с засеянными первичными
/// записями (ОР-6.0.2) — фото и видео, проиндексированные в периоде, их лица и отбраковка, сессии периода и автосверки,
/// кандидаты по статусам; записи вне периода, другого подразделения, выше грифа и кандидаты невидимой сессии в счёт
/// не входят (ТБ-020/021); гриф — максимум вошедшего; отбор по подразделению.
/// </summary>
/// <remarks>
/// Требуется Docker. Подразделения 801–803 и период в 2020 году не пересекаются с записями других тестов класса-фикстуры.
/// </remarks>
public sealed class MediaStatisticsTests(MediaSnapshotFixture fixture) : IClassFixture<MediaSnapshotFixture>
{
    private const int Own = 801;
    private const int Neighbour = 802;
    private const int Foreign = 803; // вне допуска субъекта

    private static readonly DateTime From = new(2020, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2020, 4, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Inside = new(2020, 3, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Before = new(2020, 2, 28, 10, 0, 0, DateTimeKind.Utc);

    [Fact(DisplayName = "ТФ-ОТЧ-07: счётчики сходятся с первичными записями; вне периода, подразделения и грифа — не в счёт; гриф — максимум вошедшего")]
    public async Task Counters_match_primary_records()
    {
        await SeedAsync();
        var statistics = new MediaStatistics(fixture.Media, new AllowAllAccessPolicy());
        var access = InvestigationTestKit.Access(1, 2, Own, Neighbour);

        var result = await statistics.RecognitionAsync(From, To, divisionId: null, access);

        result.Divisions.Select(d => d.DivisionId).ShouldBe([Own, Neighbour]);
        var own = result.Divisions[0];
        own.ImagesProcessed.ShouldBe(2);  // третье фото — до периода, видео гриф 3 — выше допуска
        own.VideosProcessed.ShouldBe(1);
        own.FacesFound.ShouldBe(4);       // 1 + 1 + 2 на видео; лица невидимого и внепериодного носителя — нет
        own.FacesRejected.ShouldBe(1);
        own.Searches.ShouldBe(2);         // ручная и автосверка периода; сессия до периода — нет
        own.AutoSearches.ShouldBe(1);
        own.Matches.ShouldBe(4);          // кандидат выше грифа — не в счёт
        own.Confirmed.ShouldBe(1);
        own.Rejected.ShouldBe(1);
        own.Undetermined.ShouldBe(1);
        own.Pending.ShouldBe(1);

        var neighbour = result.Divisions[1];
        neighbour.ImagesProcessed.ShouldBe(1);
        neighbour.FacesFound.ShouldBe(0);
        neighbour.Searches.ShouldBe(0);

        result.MaxClassification.ShouldBe((short)2);

        // Отбор по подразделению — только его строки.
        var onlyNeighbour = await statistics.RecognitionAsync(From, To, Neighbour, access);
        onlyNeighbour.Divisions.ShouldHaveSingleItem().DivisionId.ShouldBe(Neighbour);
        onlyNeighbour.MaxClassification.ShouldBe((short)1);

        // Подразделение вне допуска по номеру не раскрывается — пусто, гриф 0.
        var foreign = await statistics.RecognitionAsync(From, To, Foreign, access);
        foreign.Divisions.ShouldBeEmpty();
        foreign.MaxClassification.ShouldBe((short)0);
    }

    private async Task SeedAsync()
    {
        await using var db = fixture.Media.CreateDbContext();

        // Носители подразделения Own: два фото и видео в периоде, фото до периода, видео выше допуска (гриф 3).
        var photo1 = NewAsset(MediaKind.Image, Own, 2, Inside);
        var photo2 = NewAsset(MediaKind.Image, Own, 1, Inside);
        var video = NewAsset(MediaKind.Video, Own, 2, Inside);
        var early = NewAsset(MediaKind.Image, Own, 1, Before);
        var secret = NewAsset(MediaKind.Video, Own, 3, Inside);
        var audio = NewAsset(MediaKind.Audio, Own, 1, Inside); // у аудио лиц нет — не «обработано фото/видео»
        var neighbourPhoto = NewAsset(MediaKind.Image, Neighbour, 1, Inside);
        var foreignPhoto = NewAsset(MediaKind.Image, Foreign, 1, Inside);
        db.Assets.AddRange(photo1, photo2, video, early, secret, audio, neighbourPhoto, foreignPhoto);
        await db.SaveChangesAsync();

        db.Faces.AddRange(
            NewFace(photo1, acceptable: true),
            NewFace(photo2, acceptable: false),
            NewFace(video, acceptable: true),
            NewFace(video, acceptable: true),
            NewFace(early, acceptable: false),
            NewFace(secret, acceptable: true),
            NewFace(foreignPhoto, acceptable: true));
        await db.SaveChangesAsync();
        var target = db.Faces.First(f => f.AssetId == video.Id);

        // Сессии: ручная и автосверка в периоде, сессия до периода; кандидаты разных статусов, один — выше допуска.
        var manual = NewSession(Own, Inside, SessionOrigin.Operator);
        var auto = NewSession(Own, Inside, SessionOrigin.SystemSuggestion);
        var old = NewSession(Own, Before, SessionOrigin.Operator);
        var foreignSession = NewSession(Foreign, Inside, SessionOrigin.Operator);
        db.SearchSessions.AddRange(manual, auto, old, foreignSession);
        await db.SaveChangesAsync();

        db.SearchCandidates.AddRange(
            NewCandidate(manual, 1, target, CandidateStatus.Confirmed, 2),
            NewCandidate(manual, 2, target, CandidateStatus.Rejected, 1),
            NewCandidate(manual, 3, target, CandidateStatus.Undetermined, 1),
            NewCandidate(auto, 1, target, CandidateStatus.PendingVerifier, 1),
            NewCandidate(auto, 2, target, CandidateStatus.Confirmed, 3), // выше допуска — не в счёт
            NewCandidate(old, 1, target, CandidateStatus.Confirmed, 1),   // сессия до периода
            NewCandidate(foreignSession, 1, target, CandidateStatus.Confirmed, 1)); // сессия вне допуска
        await db.SaveChangesAsync();
    }

    private static MediaAsset NewAsset(MediaKind kind, int divisionId, short classification, DateTime indexedAt) => new()
    {
        Kind = kind,
        OriginalFileName = "файл",
        StoredFileName = Guid.NewGuid().ToString("N"),
        ContentType = kind == MediaKind.Video ? "video/mp4" : kind == MediaKind.Audio ? "audio/ogg" : "image/jpeg",
        ContentHash = Guid.NewGuid().ToString("N"),
        ByteSize = 1,
        Classification = classification,
        DivisionId = divisionId,
        IndexStatus = kind == MediaKind.Audio ? MediaIndexStatus.NotApplicable : MediaIndexStatus.Indexed,
        IndexedAt = indexedAt,
    };

    private static Face NewFace(MediaAsset asset, bool acceptable) => new()
    {
        AssetId = asset.Id,
        BoxX = 1, BoxY = 1, BoxWidth = 40, BoxHeight = 40,
        Landmarks = new float[10],
        DetectionScore = 0.9f,
        QualityScore = acceptable ? 0.8f : 0.2f,
        QualityAcceptable = acceptable,
        Classification = asset.Classification,
        DivisionId = asset.DivisionId,
    };

    private static SearchSession NewSession(int divisionId, DateTime createdAt, SessionOrigin origin) => new()
    {
        CaseId = 1,
        AuthorizationRef = "Поручение № 1",
        Scope = SearchScopeKind.CurrentCase,
        CaseIds = [1],
        ProbeSha256 = Guid.NewGuid().ToString("N"),
        TopK = 20,
        DetectorVersion = "yunet-test",
        EmbedderVersion = "sface-test",
        HnswEfSearch = 200,
        Classification = 1,
        DivisionId = divisionId,
        Origin = origin,
        CreatedAt = createdAt,
    };

    // Место в выдаче уникально в сессии (ix_search_candidate_session_id_rank).
    private static SearchCandidate NewCandidate(SearchSession session, int rank, Face face, CandidateStatus status, short classification) => new()
    {
        SessionId = session.Id,
        Rank = rank,
        FaceId = face.Id,
        AssetId = face.AssetId,
        CosineDistance = 0.1,
        ModelVersion = "sface-test",
        Classification = classification,
        DivisionId = face.DivisionId,
        Status = status,
    };
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Verification;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Modules.Media.UI;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Когда, откуда и где снят материал кандидата (ТФ-ПЛ-02): время съёмки и источник — из каталога под решёткой, место —
/// из привязки к доступному делу; строка показа не выдумывает время съёмки; карточка пары получает сведения о материале.
/// </summary>
public sealed class MaterialContextTests
{
    private static readonly AccessContext Access = new("7", 2, [1]);
    private static readonly DateTime Uploaded = new(2026, 10, 1, 8, 20, 0, DateTimeKind.Utc);
    private static readonly int[] VisibleOnly = [30];

    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();

    [Fact(DisplayName = "Сведения о материале: дата и источник — из каталога, место — из привязки; носителя вне допуска нет, место для него не спрашивается")]
    public async Task Reader_combines_catalog_and_places()
    {
        _catalog.ListMaterialInfoAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, AssetMaterialInfo>
            {
                [30] = new(30, new DateTimeOffset(2022, 4, 18, 19, 3, 0, TimeSpan.FromHours(6)), Uploaded, "Материалы ОРМ"),
            });
        _caseScope.ListAssetPlacesAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [30] = "г. Бишкек" });

        var materials = await new MaterialContextReader(_catalog, _caseScope).ReadAsync([30, 31, 30], Access);

        var material = materials.ShouldHaveSingleItem().Value;
        material.Source.ShouldBe("Материалы ОРМ");
        material.Place.ShouldBe("г. Бишкек");
        await _caseScope.Received(1).ListAssetPlacesAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(VisibleOnly)), Access, Arg.Any<CancellationToken>());

        (await new MaterialContextReader(_catalog, _caseScope).ReadAsync([], Access)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Строка материала: «снято …» при известном времени съёмки, иначе «загружено …»; источник и место — через точку")]
    public void Material_line_never_invents_capture_time()
    {
        var captured = new DateTimeOffset(2022, 12, 8, 14, 8, 0, TimeSpan.Zero);
        var full = new MaterialContext(captured, Uploaded, " Материалы ОРМ ", "г. Бишкек");
        full.MaterialLine().ShouldBe($"снято {captured.ToLocalTime():dd.MM.yyyy} · Материалы ОРМ · г. Бишкек");
        full.MaterialLine(withTime: true).ShouldStartWith($"снято {captured.ToLocalTime():dd.MM.yyyy HH:mm}");

        var bare = new MaterialContext(null, Uploaded, null, "  ");
        bare.MaterialLine().ShouldBe($"загружено {Uploaded.ToLocalTime():dd.MM.yyyy}");
    }

    [Fact(DisplayName = "Карточка пары получает сведения о материале своего носителя")]
    public async Task Candidate_pair_carries_material()
    {
        var subjects = Substitute.For<ISubjectProvider>();
        subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        var policy = Substitute.For<IVerificationPolicy>();
        policy.CanActAsync(Arg.Any<VerificationStage>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        var store = Substitute.For<ISearchSessionStore>();
        store.GetCandidateAsync(11, Access, Arg.Any<CancellationToken>()).Returns(new SearchCandidateRow(
            Id: 11, SessionId: 5, CaseId: 3, Rank: 1, FaceId: 100, AssetId: 50, FrameIndex: null, FrameTimestampMs: null,
            CosineDistance: 0.2, CropStoredFileName: "crop.jpg", ModelVersion: "sface-1", Classification: 2, DivisionId: 1,
            Status: CandidateStatus.Candidate, PersonRef: null, Decisions: []));
        store.GetAsync(5, Access, Arg.Any<CancellationToken>()).Returns(new SearchSessionRow(
            5, 3, "Постановление № 1", SearchScopeKind.CurrentCase, [3], "sha", null, null, 5, 0.5,
            "yunet-1", "sface-1", 2, 1, 7, DateTime.UtcNow, 1));
        var materials = Substitute.For<IMaterialContextReader>();
        var material = new MaterialContext(null, Uploaded, "Материалы ОРМ", "г. Бишкек");
        materials.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, MaterialContext> { [50] = material });

        var item = (await new GetCandidatePairQuery.Handler(subjects, policy, accessProvider, store, materials)
            .Handle(new GetCandidatePairQuery(11, VerificationStage.Expert), CancellationToken.None)).Data.ShouldNotBeNull();

        item.Material.ShouldBe(material);
    }
}

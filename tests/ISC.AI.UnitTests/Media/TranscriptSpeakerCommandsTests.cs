using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Transcripts;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Правки дикторов расшифровки (ТФ-МЕД-19, ADR-0062): право как у повторной расшифровки, носитель под решёткой и в делах
/// субъекта, недоступный неотличим от несуществующего (ТБ-020/021); исходы хранилища — понятным текстом; журнал — номера
/// без имён (ТБ-032); правила команд; действующий диктор по цепочке «слит в».
/// </summary>
public sealed class TranscriptSpeakerCommandsTests
{
    private const int AssetId = 5;
    private static readonly AccessContext Access = new("42", 2, [7]);

    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IMediaStore _store = Substitute.For<IMediaStore>();

    public TranscriptSpeakerCommandsTests()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _catalog.GetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(Asset());
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(true);
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _store.RenameTranscriptSpeakerAsync(default, default, default, default, default).ReturnsForAnyArgs(TranscriptSpeakerEditResult.Ok);
        _store.MergeTranscriptSpeakersAsync(default, default, default, default, default).ReturnsForAnyArgs(TranscriptSpeakerEditResult.Ok);
        _store.MoveTranscriptSegmentAsync(default, default, default, default, default).ReturnsForAnyArgs(TranscriptSpeakerEditResult.Ok);
    }

    [Fact(DisplayName = "Переименование: хранилище получает носитель, номер, имя и автора правки; ответ — успех")]
    public async Task Rename_reaches_store_with_author()
    {
        var response = await new RenameTranscriptSpeakerCommand.Handler(Guard(), _store)
            .Handle(new RenameTranscriptSpeakerCommand(AssetId, 1, "объект"), CancellationToken.None);

        response.Status.ShouldBeTrue();
        await _store.Received(1).RenameTranscriptSpeakerAsync(AssetId, 1, "объект", 42, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Без права загрузки или без определённого сотрудника — отказ, хранилище не трогается")]
    public async Task No_right_or_subject_is_denied()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await Merge(1, 2)).StatusMessage.ShouldBe(TranscriptSpeakerGuard.Denied);

        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await Merge(1, 2)).StatusMessage.ShouldBe(TranscriptSpeakerGuard.Denied);

        await _store.DidNotReceiveWithAnyArgs().MergeTranscriptSpeakersAsync(default, default, default, default, default);
    }

    [Fact(DisplayName = "ТБ-020/021: носитель вне решётки или вне дел субъекта — «не найден или недоступен», как у несуществующего")]
    public async Task Inaccessible_asset_looks_missing()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(false);
        (await Move(0, 2)).StatusMessage.ShouldBe(TranscriptSpeakerGuard.NotFound);

        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(true);
        _catalog.GetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns((MediaAssetRow?)null);
        (await Move(0, 2)).StatusMessage.ShouldBe(TranscriptSpeakerGuard.NotFound);

        await _store.DidNotReceiveWithAnyArgs().MoveTranscriptSegmentAsync(default, default, default, default, default);
    }

    [Fact(DisplayName = "Исходы хранилища: объединение по кругу и перенос к слитому диктору — понятный отказ; нет диктора — «обновите расшифровку»")]
    public async Task Store_results_are_explained()
    {
        _store.MergeTranscriptSpeakersAsync(default, default, default, default, default).ReturnsForAnyArgs(TranscriptSpeakerEditResult.Invalid);
        (await Merge(1, 2)).StatusMessage!.ShouldContain("по кругу");

        _store.MoveTranscriptSegmentAsync(default, default, default, default, default).ReturnsForAnyArgs(TranscriptSpeakerEditResult.Invalid);
        (await Move(3, 2)).StatusMessage!.ShouldContain("объединён с другим");

        _store.MoveTranscriptSegmentAsync(default, default, default, default, default).ReturnsForAnyArgs(TranscriptSpeakerEditResult.NotFound);
        (await Move(3, 2)).StatusMessage!.ShouldContain("обновите расшифровку");
    }

    [Fact(DisplayName = "ТБ-032: журнал «Изменение» — номера носителя, диктора и реплики, без имени диктора")]
    public void Audit_summaries_have_numbers_only()
    {
        var rename = new RenameTranscriptSpeakerCommand(AssetId, 1, "Иванов И.И.");
        rename.AuditAction.ShouldBe(AuditAction.Modify);
        rename.AuditSummary.ShouldBe("media:transcript:asset=5:speaker=1:rename");
        rename.AuditSummary!.ShouldNotContain("Иванов");

        new MergeTranscriptSpeakersCommand(AssetId, 3, 1).AuditSummary.ShouldBe("media:transcript:asset=5:speaker=3:merge-into=1");
        new MoveTranscriptSegmentCommand(AssetId, 12, 2).AuditSummary.ShouldBe("media:transcript:asset=5:segment=12:speaker=2");
        new MoveTranscriptSegmentCommand(AssetId, 12, null).AuditSummary.ShouldBe("media:transcript:asset=5:segment=12:speaker=auto");
    }

    [Fact(DisplayName = "Правила: имя не длиннее 100 символов; нельзя объединить с самим собой; диктор переноса — положительный или пусто")]
    public void Validators()
    {
        new RenameTranscriptSpeakerValidator().Validate(new RenameTranscriptSpeakerCommand(AssetId, 1, null)).IsValid.ShouldBeTrue();
        new RenameTranscriptSpeakerValidator().Validate(new RenameTranscriptSpeakerCommand(AssetId, 1, new string('я', 101))).IsValid.ShouldBeFalse();
        new RenameTranscriptSpeakerValidator().Validate(new RenameTranscriptSpeakerCommand(AssetId, 0, "x")).IsValid.ShouldBeFalse();

        new MergeTranscriptSpeakersValidator().Validate(new MergeTranscriptSpeakersCommand(AssetId, 2, 2)).IsValid.ShouldBeFalse();
        new MergeTranscriptSpeakersValidator().Validate(new MergeTranscriptSpeakersCommand(AssetId, 2, 1)).IsValid.ShouldBeTrue();

        new MoveTranscriptSegmentValidator().Validate(new MoveTranscriptSegmentCommand(AssetId, 0, null)).IsValid.ShouldBeTrue();
        new MoveTranscriptSegmentValidator().Validate(new MoveTranscriptSegmentCommand(AssetId, 0, 0)).IsValid.ShouldBeFalse();
        new MoveTranscriptSegmentValidator().Validate(new MoveTranscriptSegmentCommand(AssetId, -1, 1)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Действующий диктор: перенос важнее автоматики, затем цепочка «слит в»; цикл обрывается, а не зацикливает")]
    public void Effective_speaker()
    {
        var merged = new Dictionary<int, int?> { [1] = null, [2] = 3, [3] = 1, [4] = null };

        TranscriptSpeakers.Effective(1, null, merged).ShouldBe(1);
        TranscriptSpeakers.Effective(2, null, merged).ShouldBe(1);   // 2 → 3 → 1
        TranscriptSpeakers.Effective(1, 4, merged).ShouldBe(4);      // перенесена сотрудником
        TranscriptSpeakers.Effective(null, null, merged).ShouldBeNull();

        var cycle = new Dictionary<int, int?> { [1] = 2, [2] = 1 };
        TranscriptSpeakers.Effective(1, null, cycle).ShouldBe(1);
        TranscriptSpeakers.DefaultLabel(7).ShouldBe("Диктор 7");
    }

    private TranscriptSpeakerGuard Guard() => new(_administration, _accessProvider, _catalog, _caseScope, _subject);

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<bool>> Merge(int number, int into) =>
        await new MergeTranscriptSpeakersCommand.Handler(Guard(), _store)
            .Handle(new MergeTranscriptSpeakersCommand(AssetId, number, into), CancellationToken.None);

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<bool>> Move(int segment, int? speaker) =>
        await new MoveTranscriptSegmentCommand.Handler(Guard(), _store)
            .Handle(new MoveTranscriptSegmentCommand(AssetId, segment, speaker), CancellationToken.None);

    private static MediaAssetRow Asset() => new(
        AssetId, MediaKind.Audio, "допрос.mp3", "0123456789abcdef0123456789abcdef.mp3", "audio/mpeg", 1024, 70_000, null, null,
        2, 7, 1, MediaIndexStatus.NotApplicable, null, null, null, null, System.DateTime.UtcNow, 0, TranscriptStatus.Done);
}

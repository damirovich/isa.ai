using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Дикторы расшифровки на настоящей БД (ТФ-МЕД-19, ADR-0062): разметка пишется с фрагментами и с грифом носителя,
/// правки — слоем поверх неё (имя, объединение без кругов, перенос реплики не к слитому диктору) с автором; текст
/// фрагментов не меняется; повторная расшифровка заменяет разметку и правки целиком; дикторы — под решёткой.
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaTranscriptSpeakerTests(MediaTranscriptFixture fixture) : IClassFixture<MediaTranscriptFixture>
{
    private static readonly AccessContext Access = new("u1", MaxClassification: 2, AllowedDivisions: [130]);

    [Fact(DisplayName = "ADR-0062: дикторы пишутся с фрагментами; правки — имя, объединение (без кругов), перенос (не к слитому) — с автором; текст не меняется")]
    public async Task Speakers_are_stored_and_edited()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var asset = await SeedAssetAsync(classification: 2, division: 130);
        await store.CompleteTranscriptionAsync(
            asset,
            [
                new TranscriptSegmentDraft(0, 0, 900, "салам", Speaker: 1),
                new TranscriptSegmentDraft(1, 1000, 1900, "кандайсың", Speaker: 2),
                new TranscriptSegmentDraft(2, 2000, 2900, "жакшы", Speaker: 3),
                new TranscriptSegmentDraft(3, 3000, 3900, "шум"),
            ],
            "m1",
            speakers: new TranscriptSpeakerMarkup(TranscriptSpeakerSource.Model, null));

        var initial = (await catalog.GetTranscriptAsync(asset, Access)).ShouldNotBeNull();
        initial.SpeakerSource.ShouldBe(TranscriptSpeakerSource.Model);
        initial.Speakers.ShouldNotBeNull().Select(s => (s.Number, s.Label, s.Segments)).ShouldBe(
            [(1, "Диктор 1", 1), (2, "Диктор 2", 1), (3, "Диктор 3", 1)]);
        initial.Segments.Select(s => s.SpeakerLabel).ShouldBe(["Диктор 1", "Диктор 2", "Диктор 3", TranscriptSpeakers.UnknownLabel]);
        initial.Segments[3].Speaker.ShouldBeNull();

        (await store.RenameTranscriptSpeakerAsync(asset, 1, "  объект  ", 42)).ShouldBe(TranscriptSpeakerEditResult.Ok);
        (await store.RenameTranscriptSpeakerAsync(asset, 1, new string('я', 101), 42)).ShouldBe(TranscriptSpeakerEditResult.Invalid);
        (await store.RenameTranscriptSpeakerAsync(asset, 9, "нет такого", 42)).ShouldBe(TranscriptSpeakerEditResult.NotFound);

        (await store.MergeTranscriptSpeakersAsync(asset, 3, 1, 42)).ShouldBe(TranscriptSpeakerEditResult.Ok);
        (await store.MergeTranscriptSpeakersAsync(asset, 1, 3, 42)).ShouldBe(TranscriptSpeakerEditResult.Invalid); // по кругу
        (await store.MergeTranscriptSpeakersAsync(asset, 2, 2, 42)).ShouldBe(TranscriptSpeakerEditResult.Invalid);

        (await store.MoveTranscriptSegmentAsync(asset, 3, 2, 42)).ShouldBe(TranscriptSpeakerEditResult.Ok);
        (await store.MoveTranscriptSegmentAsync(asset, 0, 3, 42)).ShouldBe(TranscriptSpeakerEditResult.Invalid);   // 3 слит с 1
        (await store.MoveTranscriptSegmentAsync(asset, 0, 9, 42)).ShouldBe(TranscriptSpeakerEditResult.NotFound);
        (await store.MoveTranscriptSegmentAsync(asset, 99, 2, 42)).ShouldBe(TranscriptSpeakerEditResult.NotFound);

        var edited = (await catalog.GetTranscriptAsync(asset, Access)).ShouldNotBeNull();
        edited.Segments.Select(s => (s.Speaker, s.SpeakerLabel, s.SpeakerMoved)).ShouldBe(
            [(1, "объект", false), (2, "Диктор 2", false), (1, "объект", false), (2, "Диктор 2", true)]);
        edited.Segments.Select(s => s.Text).ShouldBe(["салам", "кандайсың", "жакшы", "шум"]);
        edited.Speakers.ShouldNotBeNull().Select(s => (s.Number, s.Label, s.MergedInto, s.Segments)).ShouldBe(
            [(1, "объект", (int?)null, 2), (2, "Диктор 2", null, 2), (3, "Диктор 3", 1, 0)]);

        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            var speaker = await db.TranscriptSpeakers.AsNoTracking().SingleAsync(s => s.AssetId == asset && s.Number == 1);
            (speaker.EditedByUserId, speaker.Classification, speaker.DivisionId).ShouldBe(((int?)42, (short)2, 130));
            speaker.EditedAt.ShouldNotBeNull();
            var moved = await db.TranscriptSegments.AsNoTracking().SingleAsync(s => s.AssetId == asset && s.Index == 3);
            (moved.Speaker, moved.SpeakerOverride, moved.SpeakerEditedByUserId).ShouldBe(((short?)null, (short?)2, (int?)42));
        }

        // «Как разметила система»: перенос снимается, реплика снова без диктора — «Диктор ?».
        (await store.MoveTranscriptSegmentAsync(asset, 3, null, 42)).ShouldBe(TranscriptSpeakerEditResult.Ok);
        var reverted = (await catalog.GetTranscriptAsync(asset, Access)).ShouldNotBeNull().Segments[3];
        (reverted.Speaker, reverted.SpeakerLabel).ShouldBe(((int?)null, TranscriptSpeakers.UnknownLabel));
    }

    [Fact(DisplayName = "ADR-0062: повторная расшифровка заменяет разметку и правки целиком; причина без разметки сохраняется (до 200 символов)")]
    public async Task Retranscription_resets_speakers()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var asset = await SeedAssetAsync(classification: 1, division: 130);
        await store.CompleteTranscriptionAsync(
            asset, [new TranscriptSegmentDraft(0, 0, 900, "салам", Speaker: 1)], "m1",
            speakers: new TranscriptSpeakerMarkup(TranscriptSpeakerSource.StereoChannels, null));
        await store.RenameTranscriptSpeakerAsync(asset, 1, "объект", 42);

        await store.CompleteTranscriptionAsync(
            asset, [new TranscriptSegmentDraft(0, 0, 900, "салам")], "m2",
            speakers: new TranscriptSpeakerMarkup(TranscriptSpeakerSource.None, new string('п', 250)));

        var again = (await catalog.GetTranscriptAsync(asset, Access)).ShouldNotBeNull();
        again.Speakers.ShouldNotBeNull().ShouldBeEmpty();
        again.SpeakerSource.ShouldBe(TranscriptSpeakerSource.None);
        again.SpeakerNote.ShouldNotBeNull().Length.ShouldBe(200);
        again.Segments.ShouldHaveSingleItem().SpeakerLabel.ShouldBeNull();

        // Без сведений о разметке (старый распознаватель) — «не размечены» без причины.
        await store.CompleteTranscriptionAsync(asset, [new TranscriptSegmentDraft(0, 0, 900, "салам")], "m3");
        (await catalog.GetTranscriptAsync(asset, Access)).ShouldNotBeNull().SpeakerNote.ShouldBeNull();
    }

    [Fact(DisplayName = "ТБ-020/021: дикторы — под решёткой: строка диктора выше допуска не выдаётся, её имя не попадает в подпись")]
    public async Task Speakers_follow_lattice()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var asset = await SeedAssetAsync(classification: 1, division: 131);
        await store.CompleteTranscriptionAsync(
            asset, [new TranscriptSegmentDraft(0, 0, 900, "салам", Speaker: 1)], "m1",
            speakers: new TranscriptSpeakerMarkup(TranscriptSpeakerSource.Model, null));
        await store.RenameTranscriptSpeakerAsync(asset, 1, "Фамилия", 42);

        // Аномалия, которой хранилище не создаёт: строка диктора с грифом выше носителя — решётка режет по ней самой.
        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            await db.TranscriptSpeakers.Where(s => s.AssetId == asset).ExecuteUpdateAsync(s => s.SetProperty(e => e.Classification, (short)3));
        }

        var access = new AccessContext("u2", MaxClassification: 1, AllowedDivisions: [131]);
        var transcript = (await catalog.GetTranscriptAsync(asset, access)).ShouldNotBeNull();
        transcript.Speakers.ShouldNotBeNull().ShouldBeEmpty();
        transcript.Segments.ShouldHaveSingleItem().SpeakerLabel.ShouldBe("Диктор 1");

        (await catalog.GetTranscriptAsync(asset, access with { AllowedDivisions = [130] })).ShouldBeNull();
        (await catalog.GetTranscriptAsync(asset, access with { MaxClassification = 3 })).ShouldNotBeNull()
            .Segments.Single().SpeakerLabel.ShouldBe("Фамилия");
    }

    private async Task<int> SeedAssetAsync(short classification, int division)
    {
        await using var db = await fixture.Media.CreateDbContextAsync();
        var stored = Guid.NewGuid().ToString("N") + ".ogg";
        var asset = new MediaAsset
        {
            Kind = MediaKind.Audio,
            OriginalFileName = stored,
            StoredFileName = stored,
            ContentType = "audio/ogg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = classification,
            DivisionId = division,
            IndexStatus = MediaIndexStatus.NotApplicable,
            TranscriptStatus = TranscriptStatus.Pending,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }
}

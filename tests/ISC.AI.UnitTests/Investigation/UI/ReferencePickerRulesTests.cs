using System;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Выбор эталона мышкой (ТБ-077): источником годятся только фото и видео дела (аудио лица не несёт, ADR-0026),
/// выбрать можно только пригодное лицо (по непригодному не строится шаблон, ТО-мат-07), единственное пригодное
/// лицо выбирается само.
/// </summary>
public sealed class ReferencePickerRulesTests
{
    [Fact(DisplayName = "Источник эталона: только фото и видео; фото первыми, внутри — новые первыми")]
    public void Source_assets_are_images_then_videos_newest_first()
    {
        var assets = new[]
        {
            Asset(1, MediaKind.Video, new DateTime(2026, 9, 1)),
            Asset(2, MediaKind.Audio, new DateTime(2026, 9, 5)),
            Asset(3, MediaKind.Image, new DateTime(2026, 9, 2)),
            Asset(4, MediaKind.Image, new DateTime(2026, 9, 3)),
        };

        ReferencePickerRules.SourceAssets(assets).Select(a => a.Id).ShouldBe([4, 3, 1]);
    }

    [Fact(DisplayName = "Непригодное лицо выбрать нельзя")]
    public void Unusable_face_cannot_be_picked()
    {
        ReferencePickerRules.CanPick(Face(1, acceptable: true)).ShouldBeTrue();
        ReferencePickerRules.CanPick(Face(2, acceptable: false)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Единственное пригодное лицо выбирается само; несколько или ни одного — выбирает человек")]
    public void Single_usable_face_is_picked_automatically()
    {
        ReferencePickerRules.AutoPick([Face(1, true)]).ShouldNotBeNull().Id.ShouldBe(1);
        ReferencePickerRules.AutoPick([Face(1, false), Face(2, true)]).ShouldNotBeNull().Id.ShouldBe(2);
        ReferencePickerRules.AutoPick([Face(1, true), Face(2, true)]).ShouldBeNull();
        ReferencePickerRules.AutoPick([Face(1, false)]).ShouldBeNull();
        ReferencePickerRules.AutoPick([]).ShouldBeNull();
    }

    private static MediaAssetRow Asset(int id, MediaKind kind, DateTime createdAt) => new(
        id, kind, $"f{id}", $"s{id}", "image/jpeg", 1, null, null, null, 1, 1, null,
        MediaIndexStatus.Indexed, null, null, null, null, createdAt, 1);

    private static FaceRow Face(int id, bool acceptable) => new(
        id, 10, null, null, 0, 0, 10, 10, 0.9f, acceptable ? 0.8f : 0.1f, acceptable, acceptable ? null : "размыто", "c.jpg", null, 1, 1);
}

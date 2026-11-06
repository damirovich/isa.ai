using System;
using System.Collections.Generic;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Exchange;
using ISC.AI.Profile.Investigation.Data.Exchange;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Exchange;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Обмен между офисами, этап 3 (ADR-0068 v0.3), без базы: поиск того же лица по месту в кадре, сверка исходов с манифестом
/// (ЦС-08), провенанс импортированного появления, настройки сопоставления и проверка решения Руководителя.
/// </summary>
public sealed class ExchangeStage3Tests
{
    private static ExchangeFace SourceFace(long timestampMs, float x, float y, float width = 60, float height = 60) =>
        new(SourceId: 1, AssetSourceId: 1, FrameIndex: (int)(timestampMs / 1000), TimestampMs: timestampMs, x, y, width, height);

    private static MediaExchangeFace LocalFace(int id, long timestampMs, float x, float y, float width = 60, float height = 60) =>
        new(id, AssetId: 7, FrameIndex: (int)(timestampMs / 1000), TimestampMs: timestampMs, x, y, width, height);

    [Fact(DisplayName = "Перекрытие рамок (IoU): одинаковые — 1, без пересечения — 0, половина ширины — треть")]
    public void Overlap_is_intersection_over_union()
    {
        ExchangeFaceMatcher.Overlap(0, 0, 10, 10, 0, 0, 10, 10).ShouldBe(1.0, 1e-9);
        ExchangeFaceMatcher.Overlap(0, 0, 10, 10, 20, 20, 10, 10).ShouldBe(0.0);
        ExchangeFaceMatcher.Overlap(0, 0, 10, 10, 5, 0, 10, 10).ShouldBe(50.0 / 150.0, 1e-9);
        ExchangeFaceMatcher.Overlap(0, 0, 0, 10, 0, 0, 10, 10).ShouldBe(0.0);
    }

    [Fact(DisplayName = "ТФ-ОБМ-02: то же лицо — на кадре того же таймкода с наибольшим перекрытием рамки; постороннее лицо и чужой кадр не берутся")]
    public void Same_face_is_found_by_place_in_frame()
    {
        var source = SourceFace(12_000, 100, 80);
        IReadOnlyList<MediaExchangeFace> local =
        [
            LocalFace(1, 12_000, 400, 300),     // постороннее лицо на том же кадре
            LocalFace(2, 12_000, 103, 78, 61, 62), // то же лицо: рамка чуть сдвинута
            LocalFace(3, 15_000, 100, 80),       // то же место, но другой кадр — дальше допуска
        ];

        ExchangeFaceMatcher.Find(source, local, maxTimeOffsetMs: 500, minOverlap: 0.5)!.FaceId.ShouldBe(2);
    }

    [Fact(DisplayName = "ТФ-ОБМ-02: ниже порога перекрытия или дальше допуска по времени — лица нет (лучше «не найдено», чем чужое лицо)")]
    public void Weak_or_distant_match_is_not_taken()
    {
        var source = SourceFace(12_000, 100, 80);
        ExchangeFaceMatcher.Find(source, [LocalFace(1, 12_000, 140, 80)], 500, 0.5).ShouldBeNull();
        ExchangeFaceMatcher.Find(source, [LocalFace(1, 12_600, 100, 80)], 500, 0.5).ShouldBeNull();
        ExchangeFaceMatcher.Find(source, [LocalFace(1, 12_400, 100, 80)], 500, 0.5)!.FaceId.ShouldBe(1);
    }

    [Fact(DisplayName = "При равном перекрытии берётся ближайший кадр, затем меньший номер лица; у фото таймкод 0 у обоих")]
    public void Ties_prefer_nearest_frame_and_photo_works()
    {
        var source = SourceFace(12_000, 100, 80);
        ExchangeFaceMatcher.Find(source, [LocalFace(5, 12_400, 100, 80), LocalFace(6, 11_900, 100, 80)], 500, 0.5)!.FaceId.ShouldBe(6);

        var photo = SourceFace(0, 10, 10);
        ExchangeFaceMatcher.Find(photo, [LocalFace(9, 0, 11, 10)], 500, 0.5)!.FaceId.ShouldBe(9);
    }

    [Fact(DisplayName = "ЦС-08: сумма исходов вида равна числу в пакете — «сошлась»; иначе вид и разница названы")]
    public void Reconciliation_names_the_mismatch()
    {
        var outcomes = new ExchangeOutcomes();
        outcomes.Add(ExchangeOutcomes.Cases, ExchangeOutcomes.Created);
        outcomes.Add(ExchangeOutcomes.Cases, ExchangeOutcomes.Conflict);
        outcomes.Add(ExchangeOutcomes.Persons, ExchangeOutcomes.Created, 2);
        outcomes.Add(ExchangeOutcomes.Persons, ExchangeOutcomes.Held);

        var ok = outcomes.Reconcile(new Dictionary<string, int> { [ExchangeOutcomes.Cases] = 2, [ExchangeOutcomes.Persons] = 3 });
        ok.Ok.ShouldBeTrue();
        ok.Text.ShouldContain("дела: 2 в пакете");

        var wrong = outcomes.Reconcile(new Dictionary<string, int> { [ExchangeOutcomes.Cases] = 2, [ExchangeOutcomes.Persons] = 4 });
        wrong.Ok.ShouldBeFalse();
        wrong.Text.ShouldContain("объекты: 4 в пакете");
        wrong.Text.ShouldContain("НЕ СХОДИТСЯ (3)");
    }

    [Fact(DisplayName = "ТБ-073: провенанс импортированного появления — подписи и решения источника словами; нечитаемый — пусто, без падения")]
    public void Provenance_round_trips_with_labels()
    {
        var provenance = new AppearanceProvenance(
            "office-2", "isc-exchange-office-2-2026-10-2026-11-06_080932", 41,
            new ProvenanceSignature("expert", "Экспертов Э."), null, null,
            [new ProvenanceDecision("Expert", "Confirmed", "совпадение по форме ушей", new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc),
                new ProvenanceSignature("expert", null), "SingleOperator")],
            new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc));

        var parsed = AppearanceProvenance.Parse(provenance.ToJson()).ShouldNotBeNull();
        (parsed.SourceInstance, parsed.SourceAppearanceId, parsed.Expert!.Label).ShouldBe(("office-2", 41, "Экспертов Э."));
        var decision = parsed.Decisions.ShouldHaveSingleItem();
        (decision.StageLabel, decision.VerdictLabel, decision.ModeLabel, decision.By!.Label).ShouldBe(("эксперт", "подтверждено", "один оператор", "expert"));

        AppearanceProvenance.Parse(null).ShouldBeNull();
        AppearanceProvenance.Parse("{не json").ShouldBeNull();
    }

    [Fact(DisplayName = "Настройки сопоставления: по умолчанию 500 мс и 0,5; число с точкой; выход за пределы — к границе")]
    public void Face_match_settings_have_bounds()
    {
        var defaults = ExchangeSettings.Read(new ConfigurationBuilder().Build());
        (defaults.FaceMatchMaxTimeOffsetMs, defaults.FaceMatchMinOverlap, defaults.FaceMatchPollSeconds).ShouldBe((500L, 0.5, 60));

        var custom = ExchangeSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Exchange:Import:FaceMatch:MaxTimeOffsetMs"] = "999999",
            ["Exchange:Import:FaceMatch:MinOverlap"] = "0.65",
            ["Exchange:Import:FaceMatch:PollSeconds"] = "1",
        }).Build());
        (custom.FaceMatchMaxTimeOffsetMs, custom.FaceMatchMinOverlap, custom.FaceMatchPollSeconds).ShouldBe((10_000L, 0.65, 10));
    }

    [Fact(DisplayName = "Решение Руководителя: неизвестное решение, слишком длинные номер и комментарий — отказ проверки")]
    public void Decision_validator_rejects_bad_input()
    {
        var validator = new DecideExchangeConflictValidator();
        validator.Validate(new DecideExchangeConflictCommand(3, ExchangeConflictDecision.Merge, null, "то же дело")).IsValid.ShouldBeTrue();
        validator.Validate(new DecideExchangeConflictCommand(3, (ExchangeConflictDecision)9, null, null)).IsValid.ShouldBeFalse();
        validator.Validate(new DecideExchangeConflictCommand(0, ExchangeConflictDecision.Skip, null, null)).IsValid.ShouldBeFalse();
        validator.Validate(new DecideExchangeConflictCommand(3, ExchangeConflictDecision.CreateNew, new string('1', 101), null)).IsValid.ShouldBeFalse();
        validator.Validate(new DecideExchangeConflictCommand(3, ExchangeConflictDecision.Skip, null, new string('к', 1001))).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Журнал решения: номер конфликта, решение и новый номер — без лишнего")]
    public void Decision_audit_summary_is_compact()
    {
        new DecideExchangeConflictCommand(12, ExchangeConflictDecision.CreateNew, " ОУ-2/17 (office-2) ", null).AuditSummary
            .ShouldBe("investigation:exchange:conflict:12;decision=CreateNew;number=ОУ-2/17 (office-2)");
    }
}

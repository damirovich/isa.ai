using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application.Features.Clearances;
using ISC.AI.Profile.Inspector.Application.Features.Loading;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Domain.Enums;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Шкала грифов платформы (ADR-0030): пять уровней с названиями, маркировкой и пояснениями; значения прежней
/// шкалы выше 4 показываются высшим уровнем; все формы и валидаторы берут предел из одной шкалы.
/// </summary>
public sealed class ClassificationLevelsTests
{
    [Theory(DisplayName = "Уровни шкалы: название, маркировка на документе, признак государственной тайны")]
    [InlineData(0, "Без грифа", "НЕСЕКРЕТНО", false)]
    [InlineData(1, "ДСП", "ДЛЯ СЛУЖЕБНОГО ПОЛЬЗОВАНИЯ", false)]
    [InlineData(2, "Секретно", "СЕКРЕТНО", true)]
    [InlineData(3, "Совершенно секретно", "СОВЕРШЕННО СЕКРЕТНО", true)]
    [InlineData(4, "Особой важности", "ОСОБОЙ ВАЖНОСТИ", true)]
    public void Levels_have_names_and_markings(short level, string label, string marking, bool stateSecret)
    {
        ClassificationLevels.Label(level).ShouldBe(label);
        ClassificationLevels.Marking(level).ShouldBe(marking);
        ClassificationLevels.IsStateSecret(level).ShouldBe(stateSecret);
        ClassificationLevels.IsValid(level).ShouldBeTrue();
        ClassificationLevels.Description(level).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact(DisplayName = "Шкала: пять уровней 0..4 по возрастанию; «несекретно» и ДСП — не государственная тайна")]
    public void Scale_is_five_ascending_levels()
    {
        ClassificationLevels.Max.ShouldBe((short)4);
        ClassificationLevels.All.ShouldBe([(short)0, (short)1, (short)2, (short)3, (short)4]);
        ClassificationLevels.Description(ClassificationLevels.Unclassified).ShouldContain("не является степенью");
        ClassificationLevels.Description(ClassificationLevels.ForOfficialUse).ShouldContain("несекретная");
    }

    [Theory(DisplayName = "Прежняя шкала: значения выше 4 (журнал аудита, 32767) показываются как «Особой важности», но вне шкалы для ввода")]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(short.MaxValue)]
    public void Legacy_values_are_shown_as_top_level(short legacy)
    {
        ClassificationLevels.Normalize(legacy).ShouldBe(ClassificationLevels.Max);
        ClassificationLevels.Label(legacy).ShouldBe("Особой важности");
        ClassificationLevels.Marking(legacy).ShouldBe("ОСОБОЙ ВАЖНОСТИ");
        ClassificationLevels.IsValid(legacy).ShouldBeFalse();
    }

    [Fact(DisplayName = "Валидаторы допуска, дела, документа и загрузки корпуса принимают 0..4 и отклоняют 5 и отрицательные")]
    public void Validators_use_the_shared_scale()
    {
        SetUserClearanceValidator.MaxClassification.ShouldBe(ClassificationLevels.Max);
        CreateCaseValidator.MaxClassification.ShouldBe(ClassificationLevels.Max);

        var clearance = new SetUserClearanceValidator();
        clearance.Validate(new SetUserClearanceCommand(7, 4, [1])).IsValid.ShouldBeTrue();
        clearance.Validate(new SetUserClearanceCommand(7, 5, [1])).IsValid.ShouldBeFalse();

        var caseValidator = new CreateCaseValidator();
        var validCase = new CreateCaseCommand("УД-1", "Кража", CaseKind.CriminalCase, new DateOnly(2026, 9, 1), null, 5, 4);
        caseValidator.Validate(validCase).IsValid.ShouldBeTrue();
        caseValidator.Validate(validCase with { Classification = 5 }).IsValid.ShouldBeFalse();

        var ingest = new IngestFileValidator();
        var file = new IngestFileCommand([1], "a.txt", "приказ", 3, 7);
        ingest.Validate(file).IsValid.ShouldBeTrue();
        ingest.Validate(file with { Classification = 5 }).IsValid.ShouldBeFalse();
        ingest.Validate(file with { Classification = -1 }).IsValid.ShouldBeFalse();

        var bundle = new ImportBundleValidator();
        bundle.Validate(new ImportBundleCommand("m.json", 7, 0)).IsValid.ShouldBeTrue();
        bundle.Validate(new ImportBundleCommand("m.json", 7, 9)).IsValid.ShouldBeFalse();
    }
}

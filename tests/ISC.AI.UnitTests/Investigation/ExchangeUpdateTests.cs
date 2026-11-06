using System;
using System.Collections.Generic;
using System.Text.Json;
using ISC.AI.Profile.Investigation.Application.Features.Exchange;
using ISC.AI.Profile.Investigation.Data.Exchange;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Обмен между офисами, версия 0.4 (ADR-0068): перенос изменений уже принятых записей без базы — трёхстороннее сравнение
/// поля (снимок, источник, получатель), вид значений в снимке, поля конфликта изменений и проверка решения.
/// </summary>
public sealed class ExchangeUpdateTests
{
    private static ExchangeUpdateContext Context() => new()
    {
        Source = "office-2",
        ImportId = 1,
        Note = "изменение из office-2",
        Users = new Dictionary<int, int>(),
        References = new Dictionary<int, int>(),
        Persons = new Dictionary<int, int> { [10] = 110 },
        ImportFinished = new Dictionary<int, DateTime?>(),
        MergedCases = new HashSet<int>(),
        Names = new ExchangeNames(),
        Now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc),
    };

    private static Dictionary<string, string?> Parse(string snapshot) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(snapshot).ShouldNotBeNull();

    [Theory(DisplayName = "Трёхстороннее сравнение: не менялось у источника — оставить; не трогали у получателя — записать; одинаково у обоих — только снимок; по-разному — конфликт")]
    [InlineData("а", "а", "б", ExchangeThreeWay.Action.Keep)]
    [InlineData("а", "б", "а", ExchangeThreeWay.Action.Apply)]
    [InlineData("а", "б", "б", ExchangeThreeWay.Action.Converged)]
    [InlineData("а", "б", "в", ExchangeThreeWay.Action.Conflict)]
    [InlineData(null, "б", null, ExchangeThreeWay.Action.Apply)]
    [InlineData("а", null, "а", ExchangeThreeWay.Action.Apply)]
    [InlineData(null, null, "в", ExchangeThreeWay.Action.Keep)]
    [InlineData("а", null, "в", ExchangeThreeWay.Action.Conflict)]
    public void Three_way_decision(string? baseValue, string? source, string? local, ExchangeThreeWay.Action expected) =>
        ExchangeThreeWay.Decide(baseValue, source, local).ShouldBe(expected);

    [Fact(DisplayName = "Снимок — в виде получателя: строки без крайних пробелов, пустое — «нет значения», перечисления и гриф — числом")]
    public void Snapshot_is_canonical()
    {
        var snapshot = Parse(ExchangeRecordUpdater.SnapshotOf(new ExchangeAddress(5, AddressKind.Residence, "  г. Бишкек, ул. Ленина, 15 ", "   ", 2)));

        snapshot["text"].ShouldBe("г. Бишкек, ул. Ленина, 15");
        snapshot["notes"].ShouldBeNull();
        snapshot["kind"].ShouldBe(((int)AddressKind.Residence).ToString(System.Globalization.CultureInfo.InvariantCulture));
        snapshot["classification"].ShouldBe("2");
    }

    [Fact(DisplayName = "Снимок объекта: связь — свой номер объекта, и только у роли «связь»; год рождения — из даты")]
    public void Person_snapshot_uses_receiver_numbers()
    {
        var person = new ExchangePerson(
            SourceId: 11, CaseSourceId: 1, DisplayName: "Сидоров Семён", IsUnidentified: false, UnidentifiedNumber: null, Role: PersonRole.Link,
            RoleInCase: null, BirthDate: new DateOnly(1990, 5, 1), BirthYear: 1985, BirthPlace: null, WorkPlace: null, Residence: null, Sex: null,
            Alias: null, Citizenship: null, Orientation: null, Notes: null, LinkedToPersonSourceId: 10, LinkTypeSourceId: null,
            Classification: 1, DivisionSourceId: 1, Addresses: [], Phones: [], Vehicles: []);

        var snapshot = Parse(ExchangeRecordUpdater.SnapshotOf(person, Context()));

        JsonSerializer.Deserialize<string?[]>(snapshot["role"]!).ShouldNotBeNull()
            .ShouldBe([((int)PersonRole.Link).ToString(System.Globalization.CultureInfo.InvariantCulture), "110", null]);
        JsonSerializer.Deserialize<string?[]>(snapshot["birth"]!).ShouldNotBeNull().ShouldBe(["1990-05-01", "1990"]);

        // У роли не «связь» связь в снимок не попадает — как и при заведении записи (ограничение таблицы).
        var target = Parse(ExchangeRecordUpdater.SnapshotOf(person with { Role = PersonRole.Target }, Context()));
        JsonSerializer.Deserialize<string?[]>(target["role"]!).ShouldNotBeNull()[1].ShouldBeNull();
    }

    [Fact(DisplayName = "Снимок сводки — содержимое той же нормализацией, что при вводе: порядок полей и пробелы JSON не дают ложного изменения")]
    public void Report_snapshot_normalizes_content()
    {
        var content = new CaseReportContent(Conclusion: "  Встреча подтверждена.  ");
        var report = new ExchangeReport(
            SourceId: 7, CaseSourceId: 1, PersonSourceId: 10, Kind: CaseReportKind.SummaryOn, ReportDate: new DateOnly(2026, 3, 3), IsActive: true,
            CreatedByUserSourceId: null, Revision: 2, ContentJson: "{ \"conclusion\" : \"  Встреча подтверждена.  \" }",
            RevisionAuthorUserSourceId: null, RevisionAt: DateTime.UtcNow, Classification: 1, DivisionSourceId: 1);

        var snapshot = Parse(ExchangeRecordUpdater.SnapshotOf(report, Context()));

        snapshot["content"].ShouldBe(content.Normalize(CaseReportKind.SummaryOn).ToJson());
        snapshot["person"].ShouldBe("110");
    }

    [Fact(DisplayName = "Поля конфликта изменений читаются из строки конфликта; пусто или битое — пусто, без падения")]
    public void Changes_round_trip()
    {
        var changes = new List<ExchangeFieldChange>
        {
            new("alias", "псевдоним", null, "Ваня", "Иван-старший", null, "Ваня", "Иван-старший"),
            new("notes", "примечания", null, "брат", "сосед", "неизвестно", "брат", "сосед", BaseKnown: false),
        };

        var parsed = ExchangeRecordUpdater.ParseChanges(JsonSerializer.Serialize(changes));

        parsed.ShouldBe(changes);
        ExchangeRecordUpdater.ParseChanges(null).ShouldBeEmpty();
        ExchangeRecordUpdater.ParseChanges("{не json").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Решение по конфликту изменений — «принять версию источника» и «оставить свою» — проходит проверку; строка изменений — «конфликт изменений»")]
    public void Update_decisions_are_valid()
    {
        var validator = new DecideExchangeConflictValidator();
        validator.Validate(new DecideExchangeConflictCommand(3, ExchangeConflictDecision.AcceptSource, null, null)).IsValid.ShouldBeTrue();
        validator.Validate(new DecideExchangeConflictCommand(3, ExchangeConflictDecision.KeepLocal, null, "своё точнее")).IsValid.ShouldBeTrue();

        var row = new ExchangeConflictRow(
            1, ExchangeConflictKind.RecordUpdate, "office-2", null, DateTime.UtcNow, "дело № 1", "изменено и у источника, и у вас: наименование",
            null, 5, null, ExchangeConflictStatus.Pending, null, null, null, null, null, null, NumberClash: false, ExchangeRecordKind.Case);
        (row.IsUpdate, row.CanDecide).ShouldBe((true, true));
    }
}

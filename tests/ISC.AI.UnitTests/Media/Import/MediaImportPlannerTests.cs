using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Import;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media.Import;

/// <summary>
/// Проверка описи до запуска (ADR-0041 п. 3): файл под папкой, формат и предел, дело среди доступных оператору,
/// неоднозначный номер, повтор файла, даты; итог по делам и отпечаток подтверждения.
/// </summary>
public sealed class MediaImportPlannerTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly AccessContext Operator = new("7", 3, [1, 2]);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "iscai-plan-test-" + Guid.NewGuid().ToString("N"));
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();

    public MediaImportPlannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "дело 1"));
        File.WriteAllBytes(Path.Combine(_folder, "дело 1", "a.jpg"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_folder, "дело 1", "b.mp4"), new byte[300]);
        File.WriteAllBytes(Path.Combine(_folder, "дело 1", "c.mp3"), new byte[50]);
        File.WriteAllBytes(Path.Combine(_folder, "дело 1", "notes.docx"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_folder, "дело 1", "empty.jpg"), []);

        Targets(
            Target(10, "1/2023", classification: 2, division: 1, code: "ОН1", department: "ОН — Оперативное наблюдение"),
            Target(11, "ОТМ-5", classification: 3, division: 2, code: "999", department: "ОТМ — Оперативно-технические мероприятия"),
            Target(12, "7", classification: 1, division: 1, code: "ОН1"),
            Target(13, "7", classification: 1, division: 2, code: "999"));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact(DisplayName = "Правильные строки ложатся в свои дела с грифом и подразделением дела; итог по делам с отделом")]
    public async Task Valid_rows_become_items_with_case_regime()
    {
        var plan = await PlanAsync(
            "файл;дело;дата съёмки;источник;место;sha256",
            "дело 1/a.jpg;1/2023;14.05.2023 13:45;камера 3;двор;" + new string('a', 64),
            "дело 1/b.mp4;ОТМ-5;;;;",
            "дело 1/c.mp3; ОТМ-5 ;;;;");

        plan.Problems.ShouldBeEmpty();
        plan.Items.Count.ShouldBe(3);
        var photo = plan.Items[0];
        photo.CaseId.ShouldBe(10);
        photo.Classification.ShouldBe((short)2);
        photo.DivisionId.ShouldBe(1);
        photo.Kind.ShouldBe(MediaKind.Image);
        photo.ContentType.ShouldBe("image/jpeg");
        photo.ByteSize.ShouldBe(100);
        photo.CapturedAt.ShouldBe(new DateTimeOffset(2023, 5, 14, 13, 45, 0, TimeSpan.FromHours(6)));
        photo.ExpectedSha256.ShouldBe(new string('A', 64));
        photo.Source.ShouldBe("камера 3");
        photo.Place.ShouldBe("двор");

        plan.Items[1].Classification.ShouldBe((short)3, "гриф — дела ОТМ, а не что-то из описи");
        plan.Items[2].Kind.ShouldBe(MediaKind.Audio);

        plan.Cases.Select(c => (c.Target.Case.Number, c.FileCount, c.Bytes)).ShouldBe([("1/2023", 1, 100L), ("ОТМ-5", 2, 350L)]);
        plan.Cases[1].Target.DepartmentLabel.ShouldBe("ОТМ — Оперативно-технические мероприятия");
        plan.TotalBytes.ShouldBe(450);
        plan.DateFromMetadataCount.ShouldBe(1, "видео без даты — дата из метаданных; аудио дата не обязательна");
    }

    [Fact(DisplayName = "Дело ищется только среди доступных оператору — одним запросом на все номера описи")]
    public async Task Cases_are_resolved_with_operator_access_in_one_call()
    {
        await PlanAsync("файл;дело", "дело 1/a.jpg;1/2023", "дело 1/b.mp4;1/2023", "дело 1/c.mp3;нет такого");

        await _caseScope.Received(1).FindCasesByNumberAsync(
            Arg.Is<IReadOnlyCollection<string>>(n => n.Order().SequenceEqual(new[] { "1/2023", "нет такого" }.Order())),
            Operator, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Ошибки строк: нет файла, формат, пустой файл, неизвестное дело, повтор файла, плохая дата и сумма — с номером строки")]
    public async Task Problems_are_reported_per_row()
    {
        var plan = await PlanAsync(
            "файл;дело;дата съёмки;sha256",
            "дело 1/нет.jpg;1/2023;;",
            "дело 1/notes.docx;1/2023;;",
            "дело 1/empty.jpg;1/2023;;",
            "дело 1/a.jpg;999/1999;;",
            "дело 1/b.mp4;1/2023;когда-то;",
            "дело 1/b.mp4;1/2023;;",
            "дело 1/c.mp3;1/2023;;abc",
            "../outside.jpg;1/2023;;",
            ";;;");

        plan.Items.ShouldBeEmpty();
        var byRow = plan.Problems.ToDictionary(p => p.Row.RowNumber, p => p.Message);
        byRow[2].ShouldContain(MediaImportPaths.NotFoundMessage);
        byRow[3].ShouldContain("формат файла не поддерживается");
        byRow[4].ShouldContain("файл пуст");
        byRow[5].ShouldContain("не найдено среди доступных вам");
        byRow[6].ShouldContain("не разобрана");
        byRow[7].ShouldContain("уже указан в строке 6");
        byRow[8].ShouldContain("SHA-256");
        byRow[9].ShouldContain(MediaImportPaths.OutsideMessage);
        byRow.ShouldNotContainKey(10, "пустая строка описи пропускается, а не считается ошибкой");
    }

    [Fact(DisplayName = "Номер дела в нескольких подразделениях: без кода — ошибка с перечнем кодов, с кодом — нужное дело")]
    public async Task Ambiguous_case_number_needs_division_code()
    {
        var plan = await PlanAsync("файл;дело;подразделение", "дело 1/a.jpg;7;", "дело 1/b.mp4;7;999", "дело 1/c.mp3;7;нет");

        plan.Problems.Single(p => p.Row.RowNumber == 2).Message.ShouldContain("укажите код подразделения (ОН1, 999)");
        plan.Items.ShouldHaveSingleItem().CaseId.ShouldBe(13);
        plan.Problems.Single(p => p.Row.RowNumber == 4).Message.ShouldContain("с кодом нет не найдено");
    }

    [Fact(DisplayName = "Дата из будущего и только дата: будущее — ошибка, только дата — 00:00 и счётчик")]
    public async Task Capture_time_rules()
    {
        var plan = await PlanAsync("файл;дело;дата съёмки", "дело 1/a.jpg;1/2023;01.01.2030 10:00", "дело 1/b.mp4;1/2023;14.05.2023");

        plan.Problems.ShouldHaveSingleItem().Message.ShouldContain("в будущем");
        var item = plan.Items.ShouldHaveSingleItem();
        item.CapturedDateOnly.ShouldBeTrue();
        item.CapturedAt.ShouldBe(new DateTimeOffset(2023, 5, 14, 0, 0, 0, TimeSpan.FromHours(6)));
        plan.DateOnlyCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Файл больше предела своего вида — ошибка (фото — 200 МБ, видео — из настройки)")]
    public async Task Size_limit_by_kind()
    {
        var plan = await PlanAsync(new MediaUploadOptions(250), "файл;дело", "дело 1/b.mp4;1/2023", "дело 1/a.jpg;1/2023");

        plan.Problems.ShouldHaveSingleItem().Row.RowNumber.ShouldBe(2);
        plan.Items.ShouldHaveSingleItem().Kind.ShouldBe(MediaKind.Image);
    }

    [Fact(DisplayName = "Отпечаток: тот же итог — тот же отпечаток; другой гриф дела — другой (подтверждение устарело)")]
    public async Task Fingerprint_follows_case_regime()
    {
        var first = await PlanAsync("файл;дело", "дело 1/a.jpg;1/2023");
        var same = await PlanAsync("файл;дело", "дело 1/a.jpg;1/2023");
        same.Fingerprint.ShouldBe(first.Fingerprint);

        Targets(Target(10, "1/2023", classification: 4, division: 1, code: "ОН1"));
        var reclassified = await PlanAsync("файл;дело", "дело 1/a.jpg;1/2023");
        reclassified.Fingerprint.ShouldNotBe(first.Fingerprint);
    }

    private Task<MediaImportPlan> PlanAsync(params string[] lines) => PlanAsync(null, lines);

    private async Task<MediaImportPlan> PlanAsync(MediaUploadOptions? options, params string[] lines)
    {
        var manifest = ImportManifest.Read(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        manifest.Error.ShouldBeNull();
        return await new MediaImportPlanner(_caseScope, options)
            .PlanAsync(_folder, "опись.csv", manifest, Operator, Zone, Now);
    }

    private void Targets(params CaseImportTarget[] targets) =>
        _caseScope.FindCasesByNumberAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(call => targets.Where(t => call.Arg<IReadOnlyCollection<string>>().Contains(t.Case.Number)).ToList());

    private static CaseImportTarget Target(int id, string number, short classification, int division, string? code, string? department = null) =>
        new(new CaseScopeItem(id, number, "Дело " + number, classification, division), "Подразделение " + division, code, department);
}

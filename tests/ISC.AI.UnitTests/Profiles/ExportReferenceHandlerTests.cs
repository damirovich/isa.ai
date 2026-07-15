using ISC.AI.Abstractions.Documents;
using ISC.AI.Profile.Inspector.Application.Generation;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Экспорт справки (Э4-04): гриф результата → текстовая маркировка для экспортёра, результат — файл
/// .docx в конверте. Факт экспорта аудирует сквозное AuditBehavior (Э4-11), проверяется отдельно.
/// </summary>
public sealed class ExportReferenceHandlerTests
{
    [Fact(DisplayName = "Экспорт: гриф 1 → «ДСП» для экспортёра; на выходе .docx")]
    public async Task Maps_marking_and_returns_docx()
    {
        DocumentExportRequest? captured = null;
        var exporter = Substitute.For<IDocumentExporter>();
        exporter.ExportToDocxAsync(Arg.Do<DocumentExportRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns([1, 2, 3]);
        var response = await new ExportReferenceCommand.Handler(exporter)
            .Handle(new ExportReferenceCommand("Справка по режиму", "тело справки", Classification: 1), CancellationToken.None);

        // Гриф 1 → маркировка «ДСП», заголовок проброшен.
        captured.ShouldNotBeNull();
        captured!.ClassificationMarking.ShouldBe("ДСП");
        captured.Title.ShouldBe("Справка по режиму");

        // Конверт: Ok + файл .docx.
        response.Status.ShouldBeTrue();
        response.Data.ShouldNotBeNull();
        response.Data!.Content.Length.ShouldBe(3);
        response.Data.FileName.ShouldEndWith(".docx");
        response.Data.ContentType.ShouldContain("wordprocessingml");
    }
}

using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Profile.Inspector.Application.Features.Loading;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Use-cases загрузки корпуса (Э4-01): файл → порт, импорт пакета → импортёр; маппинг результата.
/// С 2026-08-19 оба сценария принимают материал ТОЛЬКО под действующее подразделение из справочника
/// (<see cref="DivisionRule"/>): подразделение-призрак отклоняется до обращения к порту.
/// </summary>
public sealed class LoadingHandlersTests
{
    [Fact(DisplayName = "Загрузка файла: байты + метаданные уходят в IFileIngestor; результат замаппен")]
    public async Task Ingest_file_forwards_to_port()
    {
        FileIngestionRequest? captured = null;
        var ingestor = Substitute.For<IFileIngestor>();
        ingestor.IngestFileAsync(Arg.Do<FileIngestionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(IngestionResult.Ok(documentId: 7, chunkCount: 3));

        var response = await new IngestFileCommand.Handler(ingestor, Divisions(5))
            .Handle(new IngestFileCommand([1, 2, 3], "doc.txt", "положение", Classification: 0, DivisionId: 5), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data!.Accepted.ShouldBeTrue();
        response.Data.ChunkCount.ShouldBe(3);
        response.Data.IsDuplicate.ShouldBeFalse();
        response.Data.FileName.ShouldBe("doc.txt");

        captured.ShouldNotBeNull();
        captured!.FileName.ShouldBe("doc.txt");
        captured.DocType.ShouldBe("положение");
        captured.Classification.ShouldBe((short?)0);
        captured.DivisionId.ShouldBe(5);
    }

    [Fact(DisplayName = "Загрузка файла: подразделения нет в справочнике — отказ, порт не вызывается")]
    public async Task Ingest_file_with_unknown_division_is_rejected()
    {
        var ingestor = Substitute.For<IFileIngestor>();

        var response = await new IngestFileCommand.Handler(ingestor, Divisions(1, 2, 3))
            .Handle(new IngestFileCommand([1], "doc.txt", "положение", Classification: 0, DivisionId: 10), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldContain("№10");
        await ingestor.DidNotReceiveWithAnyArgs().IngestFileAsync(default!, default);
    }

    [Fact(DisplayName = "Загрузка новой версии: SupersedesDocumentId уходит в порт; SupersededDocumentId возвращается (Э4-14)")]
    public async Task Ingest_new_version_forwards_supersedes_and_surfaces_result()
    {
        FileIngestionRequest? captured = null;
        var ingestor = Substitute.For<IFileIngestor>();
        ingestor.IngestFileAsync(Arg.Do<FileIngestionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new IngestionResult(Accepted: true, DocumentId: 12, ChunkCount: 4, RejectionReason: null,
                SupersededDocumentId: 5, SupersededChunkCount: 3));

        var response = await new IngestFileCommand.Handler(ingestor, Divisions(1))
            .Handle(
                new IngestFileCommand([1, 2, 3], "v2.txt", "положение", Classification: 0, DivisionId: 1, SupersedesDocumentId: 5),
                CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data!.SupersededDocumentId.ShouldBe(5);

        captured.ShouldNotBeNull();
        captured!.SupersedesDocumentId.ShouldBe(5);
    }

    [Fact(DisplayName = "Импорт пакета: отсутствующий манифест — NotFound, импортёр не вызывается")]
    public async Task Import_missing_manifest_not_found()
    {
        var importer = Substitute.For<IBundleImporter>();

        var response = await new ImportBundleCommand.Handler(importer, Divisions(1))
            .Handle(new ImportBundleCommand(@"C:\nope\does-not-exist\manifest.json", DivisionId: 1), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await importer.DidNotReceiveWithAnyArgs().ImportAsync(default!, default, default);
    }

    [Fact(DisplayName = "Импорт пакета: подразделения нет в справочнике — отказ ДО чтения пакета")]
    public async Task Import_with_unknown_division_is_rejected()
    {
        var importer = Substitute.For<IBundleImporter>();

        var response = await new ImportBundleCommand.Handler(importer, Divisions(1, 2, 3))
            .Handle(new ImportBundleCommand(@"C:\any\manifest.json", DivisionId: 10), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldContain("№10");
        await importer.DidNotReceiveWithAnyArgs().ImportAsync(default!, default, default);
    }

    [Fact(DisplayName = "Импорт пакета: существующий манифест → импортёр вызывается с подразделением оператора, счётчики проброшены")]
    public async Task Import_existing_manifest_runs_with_operator_division()
    {
        var path = Path.Combine(Path.GetTempPath(), "manifest-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "[]");
        try
        {
            var importer = Substitute.For<IBundleImporter>();
            importer.ImportAsync(path, 2, Arg.Any<CancellationToken>())
                .Returns(new BundleImportResult(Total: 3, Imported: 2, Duplicates: 1, Rejected: 0));

            var response = await new ImportBundleCommand.Handler(importer, Divisions(1, 2))
                .Handle(new ImportBundleCommand(path, DivisionId: 2), CancellationToken.None);

            response.Status.ShouldBeTrue();
            response.Data!.Imported.ShouldBe(2);
            response.Data.Duplicates.ShouldBe(1);
            // Подразделение оператора ПЕРЕКРЫВАЕТ записанное в пакете — именно оно уходит импортёру.
            await importer.Received(1).ImportAsync(path, 2, Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Справочник, в котором действуют только перечисленные подразделения.</summary>
    private static IDivisionAdminStore Divisions(params int[] existingIds)
    {
        var divisions = Substitute.For<IDivisionAdminStore>();
        divisions.ExistsActiveAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => existingIds.Contains(call.Arg<int>()));
        return divisions;
    }
}

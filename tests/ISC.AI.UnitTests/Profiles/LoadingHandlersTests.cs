using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Profile.Inspector.Application.Loading;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>Use-cases загрузки корпуса (Э4-01): файл → порт, импорт пакета → импортёр; маппинг результата.</summary>
public sealed class LoadingHandlersTests
{
    [Fact(DisplayName = "Загрузка файла: байты + метаданные уходят в IFileIngestor; результат замаппен")]
    public async Task Ingest_file_forwards_to_port()
    {
        FileIngestionRequest? captured = null;
        var ingestor = Substitute.For<IFileIngestor>();
        ingestor.IngestFileAsync(Arg.Do<FileIngestionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(IngestionResult.Ok(documentId: 7, chunkCount: 3));

        var response = await new IngestFileCommand.Handler(ingestor)
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

    [Fact(DisplayName = "Загрузка новой версии: SupersedesDocumentId уходит в порт; SupersededDocumentId возвращается (Э4-14)")]
    public async Task Ingest_new_version_forwards_supersedes_and_surfaces_result()
    {
        FileIngestionRequest? captured = null;
        var ingestor = Substitute.For<IFileIngestor>();
        ingestor.IngestFileAsync(Arg.Do<FileIngestionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(new IngestionResult(Accepted: true, DocumentId: 12, ChunkCount: 4, RejectionReason: null,
                SupersededDocumentId: 5, SupersededChunkCount: 3));

        var response = await new IngestFileCommand.Handler(ingestor)
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

        var response = await new ImportBundleCommand.Handler(importer)
            .Handle(new ImportBundleCommand(@"C:\nope\does-not-exist\manifest.json"), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await importer.DidNotReceive().ImportAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Импорт пакета: существующий манифест → импортёр вызывается, счётчики проброшены")]
    public async Task Import_existing_manifest_runs()
    {
        var path = Path.Combine(Path.GetTempPath(), "manifest-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "[]");
        try
        {
            var importer = Substitute.For<IBundleImporter>();
            importer.ImportAsync(path, Arg.Any<CancellationToken>()).Returns(new BundleImportResult(Total: 3, Imported: 2, Duplicates: 1, Rejected: 0));

            var response = await new ImportBundleCommand.Handler(importer)
                .Handle(new ImportBundleCommand(path), CancellationToken.None);

            response.Status.ShouldBeTrue();
            response.Data!.Imported.ShouldBe(2);
            response.Data.Duplicates.ShouldBe(1);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Harvester.Engine;
using ISC.AI.Ingestion;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Ingestion;

/// <summary>
/// Импорт пакета сборщика в контуре (Э4-08↔Э4-01): manifest.json → IngestionPort с маппингом полей;
/// считает загружено/дубли/отказ. Замыкает петлю harvester→пакет→корпус.
/// </summary>
public sealed class BundleImporterTests
{
    [Fact(DisplayName = "Импорт пакета: документы из манифеста уходят в порт; считаются загрузка/дубли")]
    public async Task Imports_manifest_into_port()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harvester-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            HarvestedDocument[] docs =
            [
                new("http://e/1", "Док 1", "текст 1", "закон", "H1", Classification: 0, DivisionId: 7),
                new("http://e/2", "Док 2", "текст 2", "закон", "H2", Classification: 0, DivisionId: 7),
            ];
            var manifestPath = await new JsonBundleWriter().WriteAsync(directory, docs);

            var captured = new List<IngestionRequest>();
            var port = Substitute.For<IIngestionPort>();
            port.IngestAsync(Arg.Do<IngestionRequest>(captured.Add), Arg.Any<CancellationToken>())
                .Returns(IngestionResult.Ok(documentId: 1, chunkCount: 3), IngestionResult.Duplicate(documentId: 2));

            var result = await new BundleImporter(port).ImportAsync(manifestPath);

            result.Total.ShouldBe(2);
            result.Imported.ShouldBe(1);
            result.Duplicates.ShouldBe(1);
            result.Rejected.ShouldBe(0);

            // Маппинг полей манифеста в запрос загрузки.
            captured.Count.ShouldBe(2);
            captured[0].DocType.ShouldBe("закон");
            captured[0].Title.ShouldBe("Док 1");
            captured[0].Classification.ShouldBe((short?)0);
            captured[0].DivisionId.ShouldBe(7);
            captured[0].Source.ShouldBe("http://e/1");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

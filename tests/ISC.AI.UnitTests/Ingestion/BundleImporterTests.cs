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

    [Fact(DisplayName = "ТБ-024: гриф и подразделение оператора перекрывают записанные в пакете")]
    public async Task Operator_classification_and_division_override_bundle_values()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harvester-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Пакет декларирует гриф 0 и подразделение 7 — но пакет собран вне контура,
            // и решает оператор: его значения обязаны победить.
            HarvestedDocument[] docs =
                [new("http://e/1", "Док", "текст", "закон", "H1", Classification: 0, DivisionId: 7)];
            var manifestPath = await new JsonBundleWriter().WriteAsync(directory, docs);

            IngestionRequest? captured = null;
            var port = Substitute.For<IIngestionPort>();
            port.IngestAsync(Arg.Do<IngestionRequest>(r => captured = r), Arg.Any<CancellationToken>())
                .Returns(IngestionResult.Ok(documentId: 1, chunkCount: 1));

            await new BundleImporter(port).ImportAsync(manifestPath, divisionId: 3, classification: 2);

            captured.ShouldNotBeNull();
            captured!.Classification.ShouldBe((short?)2);
            captured.DivisionId.ShouldBe(3);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "ТБ-024: документ без поля грифа в пакете уходит в порт как null (→ отказ), а не как открытый 0")]
    public async Task Missing_classification_field_maps_to_null_not_open_zero()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harvester-import-" + Guid.NewGuid().ToString("N"));
        var manifestPath = Path.Combine(directory, "manifest.json");
        Directory.CreateDirectory(directory);
        try
        {
            // Пакет от НЕдоверенного производителя (ТБ-001): у второго документа поле classification ОТСУТСТВУЕТ.
            // До правки не-nullable short давал бы 0 (открытый гриф) и молча индексировался; теперь → null.
            await File.WriteAllTextAsync(manifestPath, """
                [
                  {"sourceUrl":"http://e/1","title":"С грифом","text":"т","docType":"закон","contentHash":"H1","classification":1,"divisionId":7},
                  {"sourceUrl":"http://e/2","title":"Без грифа","text":"т","docType":"закон","contentHash":"H2","divisionId":7}
                ]
                """);

            var captured = new List<IngestionRequest>();
            var port = Substitute.For<IIngestionPort>();
            // Имитируем реальный fail-closed порта: null-гриф → отказ, иначе — принято.
            port.IngestAsync(Arg.Do<IngestionRequest>(captured.Add), Arg.Any<CancellationToken>())
                .Returns(ci => ci.Arg<IngestionRequest>().Classification is null
                    ? IngestionResult.Reject("нет грифа")
                    : IngestionResult.Ok(documentId: 1, chunkCount: 3));

            var result = await new BundleImporter(port).ImportAsync(manifestPath);

            // Документ с явным грифом принят, документ без поля грифа — ОТКЛОНЁН (не проиндексирован).
            result.Imported.ShouldBe(1);
            result.Rejected.ShouldBe(1);

            // Ключевое: пропущенное поле доехало до порта как null, а НЕ как 0 (открытый гриф).
            captured[0].Classification.ShouldBe((short?)1);
            captured[1].Classification.ShouldBeNull();
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

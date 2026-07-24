using System.Globalization;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Harvester.Engine;
using ISC.AI.Ingestion;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Ingestion;

/// <summary>Импортёр пакета (Э4-17): читает ШАРДИРОВАННЫЙ пакет (индекс + шарды) и шлёт каждый документ в порт.</summary>
public sealed class BundleImporterShardedTests
{
    [Fact(DisplayName = "Импорт шардированного пакета: все документы из всех шардов уходят в порт загрузки")]
    public async Task Imports_all_documents_from_shards()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harv-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Пакет из 3 документов при shardSize=2 → 2 шарда + индекс-манифест.
            var writer = new StreamingBundleWriter(shardSize: 2);
            writer.Begin(dir);
            for (var i = 0; i < 3; i++)
            {
                await writer.AppendAsync(new HarvestedDocument(
                    $"http://x/{i}", $"Док {i}", $"Текст {i}", "нпа", i.ToString(CultureInfo.InvariantCulture), Classification: 0, DivisionId: 1));
            }

            var manifestPath = await writer.CompleteAsync();

            var port = Substitute.For<IIngestionPort>();
            port.IngestAsync(Arg.Any<IngestionRequest>(), Arg.Any<CancellationToken>())
                .Returns(IngestionResult.Ok(documentId: 1, chunkCount: 1));

            var result = await new BundleImporter(port).ImportAsync(manifestPath);

            result.Total.ShouldBe(3);
            result.Imported.ShouldBe(3);
            await port.Received(3).IngestAsync(Arg.Any<IngestionRequest>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "ТНД-002: отсутствующий шард — явный отказ (импорт прерван), не молчаливый пропуск")]
    public async Task Missing_shard_throws_not_silently_skipped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harv-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new StreamingBundleWriter(shardSize: 2);
            writer.Begin(dir);
            for (var i = 0; i < 3; i++)
            {
                await writer.AppendAsync(new HarvestedDocument(
                    $"http://x/{i}", $"Док {i}", $"Текст {i}", "нпа", i.ToString(CultureInfo.InvariantCulture), Classification: 0, DivisionId: 1));
            }

            var manifestPath = await writer.CompleteAsync();

            // Удаляем один шард — пакет стал неполным (потеря/повреждение при переносе через зазор).
            var shardFile = Directory.GetFiles(dir, "*.json")
                .First(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase));
            File.Delete(shardFile);

            var port = Substitute.For<IIngestionPort>();
            port.IngestAsync(Arg.Any<IngestionRequest>(), Arg.Any<CancellationToken>())
                .Returns(IngestionResult.Ok(documentId: 1, chunkCount: 1));

            // Импорт не «успешен с урезанным Total», а явно падает — оператор узнаёт о неполноте.
            await Should.ThrowAsync<FileNotFoundException>(() => new BundleImporter(port).ImportAsync(manifestPath));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

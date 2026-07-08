using System.Globalization;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>Стриминговый шардированный писатель пакета (Э4-17): ролл шардов, индекс-манифест, чтение по индексу.</summary>
public sealed class StreamingBundleWriterTests
{
    private static HarvestedDocument Doc(int i) =>
        new($"http://x/{i}", $"Док {i}", $"Текст {i}", "нпа", i.ToString(CultureInfo.InvariantCulture), Classification: 0, DivisionId: 1);

    [Fact(DisplayName = "Писатель: ролл шардов (2/файл) + индекс-манифест; чтение документа по глобальному индексу")]
    public async Task Writes_shards_and_reads_by_index()
    {
        var dir = Path.Combine(Path.GetTempPath(), "harv-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new StreamingBundleWriter(shardSize: 2);
            writer.Begin(dir);
            for (var i = 0; i < 5; i++)
            {
                await writer.AppendAsync(Doc(i));
            }

            var manifestPath = await writer.CompleteAsync();

            File.Exists(manifestPath).ShouldBeTrue();
            Directory.GetFiles(dir, "shard-*.json").Length.ShouldBe(3); // 2 + 2 + 1
            writer.Total.ShouldBe(5);

            // Индекс 3 → шард 2 (3/2), позиция 1 → «Док 3».
            var read = await StreamingBundleWriter.ReadDocumentAsync(dir, globalIndex: 3, shardSize: 2);
            read.ShouldNotBeNull();
            read!.Title.ShouldBe("Док 3");
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

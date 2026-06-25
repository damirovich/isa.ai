using System.Text.Json;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>Запись пакета импорта (Э4-08): manifest.json создаётся и читается обратно (контракт «через зазор»).</summary>
public sealed class JsonBundleWriterTests
{
    [Fact(DisplayName = "Пакет: manifest.json создан, кириллица читабельна, десериализуется обратно")]
    public async Task Writes_manifest_roundtrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "harvester-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            HarvestedDocument[] docs =
            [
                new("http://e/1", "Док 1", "текст 1", "положение", "HASH1", Classification: 0, DivisionId: 7, Language: "ru"),
            ];

            var manifestPath = await new JsonBundleWriter().WriteAsync(directory, docs);

            File.Exists(manifestPath).ShouldBeTrue();

            var json = await File.ReadAllTextAsync(manifestPath);
            json.ShouldContain("Док 1"); // кириллица не экранирована — манифест читабелен

            var roundtrip = JsonSerializer.Deserialize<HarvestedDocument[]>(json);
            roundtrip.ShouldNotBeNull();
            roundtrip!.Length.ShouldBe(1);
            roundtrip[0].Title.ShouldBe("Док 1");
            roundtrip[0].DivisionId.ShouldBe(7);
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

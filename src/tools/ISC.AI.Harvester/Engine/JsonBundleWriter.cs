using System.Text.Encodings.Web;
using System.Text.Json;
using ISC.AI.Abstractions.Harvesting;

namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Пакет импорта в виде <c>manifest.json</c> (массив <see cref="HarvestedDocument"/> с текстом внутри).
/// Кириллица не экранируется (читабельный манифест). Этот же формат читает импортёр в контуре.
/// </summary>
public sealed class JsonBundleWriter : IBundleWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <inheritdoc />
    public async Task<string> WriteAsync(
        string outputDirectory,
        IReadOnlyCollection<HarvestedDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(documents);

        Directory.CreateDirectory(outputDirectory);
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        var json = JsonSerializer.Serialize(documents, Options);
        await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
        return manifestPath;
    }
}

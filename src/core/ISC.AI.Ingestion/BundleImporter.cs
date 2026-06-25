using System.Text.Json;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Abstractions.Ingestion;

namespace ISC.AI.Ingestion;

/// <summary>
/// Импорт пакета сборщика (Э4-08↔Э4-01): <c>manifest.json</c> → на каждый <see cref="HarvestedDocument"/>
/// вызывает <see cref="IIngestionPort"/>. Гриф/подразделение приходят из манифеста (декларированы вне
/// контура); порт всё равно проверяет их fail-closed (ТБ-024) и дедуплицирует (ТНД-002).
/// </summary>
public sealed class BundleImporter(IIngestionPort port) : IBundleImporter
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<BundleImportResult> ImportAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        await using var stream = File.OpenRead(manifestPath);
        var documents = await JsonSerializer.DeserializeAsync<List<HarvestedDocument>>(stream, Options, cancellationToken)
            ?? [];

        var imported = 0;
        var duplicates = 0;
        var rejected = 0;

        foreach (var document in documents)
        {
            var result = await port.IngestAsync(
                new IngestionRequest(
                    DocType: document.DocType,
                    Title: document.Title,
                    Text: document.Text,
                    Classification: document.Classification,
                    DivisionId: document.DivisionId,
                    Source: document.SourceUrl,
                    Metadata: document.Metadata),
                cancellationToken);

            if (!result.Accepted)
            {
                rejected++;
            }
            else if (result.ChunkCount == 0)
            {
                duplicates++; // принято, но чанков 0 — идемпотентный дубликат (ТНД-002).
            }
            else
            {
                imported++;
            }
        }

        return new BundleImportResult(documents.Count, imported, duplicates, rejected);
    }
}

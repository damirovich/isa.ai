using System.Runtime.CompilerServices;
using System.Text.Json;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Abstractions.Ingestion;

namespace ISC.AI.Ingestion;

/// <summary>
/// Импорт пакета сборщика (Э4-08↔Э4-01): читает <c>manifest.json</c> и на каждый <see cref="HarvestedDocument"/>
/// вызывает <see cref="IIngestionPort"/>. Поддержаны ДВА формата пакета: одиночный массив (малые пакеты) и
/// шардированный индекс (<see cref="BundleManifest"/>) для больших корпусов 170К+ (Э4-17). Гриф/подразделение
/// приходят из манифеста (декларированы вне контура). Пакет — от НЕдоверенного производителя (ТБ-001):
/// поля грифа/подразделения в <see cref="HarvestedDocument"/> nullable, поэтому пропущенное в JSON поле
/// доезжает до порта как <see langword="null"/> и отклоняется fail-closed (ТБ-024), а не индексируется как
/// открытое; порт также дедуплицирует (ТНД-002).
/// </summary>
public sealed class BundleImporter(IIngestionPort port) : IBundleImporter
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<BundleImportResult> ImportAsync(
        string manifestPath, int? divisionId = null, short? classification = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var total = 0;
        var imported = 0;
        var duplicates = 0;
        var rejected = 0;

        await foreach (var document in ReadDocumentsAsync(manifestPath, cancellationToken))
        {
            total++;
            var result = await port.IngestAsync(
                new IngestionRequest(
                    DocType: document.DocType,
                    Title: document.Title,
                    Text: document.Text,
                    // Перекрытие оператора важнее записанного в пакете (см. IBundleImporter, ТБ-024).
                    Classification: classification ?? document.Classification,
                    DivisionId: divisionId ?? document.DivisionId,
                    Source: document.SourceUrl,
                    // Пакет даты не несёт (ЦБД отдаёт только заголовок) — берём из заголовка, где она
                    // у НПА всегда есть («от 28 октября 2021 года»); не распознана — null, не отказ.
                    DocDate: TitleDateParser.TryParse(document.Title),
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

        return new BundleImportResult(total, imported, duplicates, rejected);
    }

    // Читает документы из пакета: массив (старый формат) ИЛИ шарды по индексу-манифесту (Э4-17) — потоково.
    private static async IAsyncEnumerable<HarvestedDocument> ReadDocumentsAsync(
        string manifestPath, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!await IsShardedAsync(manifestPath, cancellationToken))
        {
            await foreach (var document in ReadArrayAsync(manifestPath, cancellationToken))
            {
                yield return document;
            }

            yield break;
        }

        BundleManifest manifest;
        await using (var stream = File.OpenRead(manifestPath))
        {
            manifest = await JsonSerializer.DeserializeAsync<BundleManifest>(stream, Options, cancellationToken)
                ?? new BundleManifest(0, 0, []);
        }

        var directory = Path.GetDirectoryName(manifestPath) ?? ".";
        foreach (var shard in manifest.Shards)
        {
            var shardPath = Path.Combine(directory, shard);
            if (!File.Exists(shardPath))
            {
                // Явный отказ, НЕ молчаливый пропуск (ТНД-002, принцип «явный отказ» — ср. CompositeTextExtractor,
                // FileIngestionService): пропущенный шард = НЕПОЛНЫЙ корпус. Пакет от недоверенного производителя,
                // перенос через зазор (ТБ-001/051) — шард мог быть потерян/повреждён. Оператор обязан узнать,
                // а не увидеть ложный «успех» с молча урезанным Total. Импорт идемпотентен → повтор после
                // исправления пакета безопасен (дедуп по content_hash).
                throw new FileNotFoundException(
                    $"Шард пакета не найден: «{shard}». Пакет неполон — импорт прерван (ТНД-002).", shardPath);
            }

            await foreach (var document in ReadArrayAsync(shardPath, cancellationToken))
            {
                yield return document;
            }
        }
    }

    private static async IAsyncEnumerable<HarvestedDocument> ReadArrayAsync(
        string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var documents = await JsonSerializer.DeserializeAsync<List<HarvestedDocument>>(stream, Options, cancellationToken) ?? [];
        foreach (var document in documents)
        {
            yield return document;
        }
    }

    // Формат по первому значимому символу: '{' — индекс шардов (новый), иначе '[' — массив (старый).
    private static async Task<bool> IsShardedAsync(string manifestPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(manifestPath);
        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        for (var i = 0; i < read; i++)
        {
            var c = (char)buffer[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return c == '{';
        }

        return false;
    }
}

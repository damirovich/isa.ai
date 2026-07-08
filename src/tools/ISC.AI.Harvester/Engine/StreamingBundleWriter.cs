using System.Text.Encodings.Web;
using System.Text.Json;
using ISC.AI.Abstractions.Harvesting;

namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Потоковый писатель пакета шардами (Э4-17) для массового сбора (170К+). Документы пишутся ПО ХОДУ:
/// буфер копится до <c>shardSize</c>, затем сбрасывается в файл-шард <c>shard-NNNNN.json</c> и очищается —
/// в памяти одновременно не более одного шарда. По завершении пишется <c>manifest.json</c> — ИНДЕКС шардов
/// (<see cref="BundleManifest"/>), а не гигантский массив. Формат читает импортёр в контуре.
/// </summary>
/// <remarks>Экземпляр — на один прогон сбора (хранит состояние); создаётся оркестратором, не через DI.</remarks>
public sealed class StreamingBundleWriter(int shardSize = 2000)
{
    private static readonly JsonSerializerOptions ShardOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly List<HarvestedDocument> _buffer = [];
    private readonly List<string> _shards = [];
    private string _dir = string.Empty;
    private int _total;

    /// <summary>Каталог пакета.</summary>
    public string Directory => _dir;

    /// <summary>Всего записано документов.</summary>
    public int Total => _total;

    /// <summary>Размер шарда (документов на файл).</summary>
    public int ShardSize => shardSize;

    /// <summary>Начинает пакет в каталоге (создаёт его).</summary>
    public void Begin(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _dir = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    /// <summary>Добавляет документ в пакет (сбрасывает шард на диск при заполнении буфера).</summary>
    public async Task AppendAsync(HarvestedDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        _buffer.Add(document);
        _total++;
        if (_buffer.Count >= shardSize)
        {
            await FlushShardAsync(cancellationToken);
        }
    }

    /// <summary>Дописывает остаток и пишет индекс-манифест. Возвращает путь к <c>manifest.json</c>.</summary>
    public async Task<string> CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_buffer.Count > 0)
        {
            await FlushShardAsync(cancellationToken);
        }

        var manifest = new BundleManifest(_total, shardSize, _shards);
        var path = Path.Combine(_dir, "manifest.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, ManifestOptions), cancellationToken);
        return path;
    }

    private async Task FlushShardAsync(CancellationToken cancellationToken)
    {
        var name = $"shard-{_shards.Count + 1:00000}.json";
        var json = JsonSerializer.Serialize(_buffer, ShardOptions);
        await File.WriteAllTextAsync(Path.Combine(_dir, name), json, cancellationToken);
        _shards.Add(name);
        _buffer.Clear();
    }

    /// <summary>
    /// Читает один документ по ГЛОБАЛЬНОМУ индексу (для просмотра текста в UI по требованию): шард =
    /// <c>index / shardSize</c>, позиция внутри = <c>index % shardSize</c>. Так текст не держим в памяти.
    /// </summary>
    public static async Task<HarvestedDocument?> ReadDocumentAsync(
        string directory, int globalIndex, int shardSize, CancellationToken cancellationToken = default)
    {
        var shardNo = (globalIndex / shardSize) + 1;
        var pos = globalIndex % shardSize;
        var path = Path.Combine(directory, $"shard-{shardNo:00000}.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var docs = await JsonSerializer.DeserializeAsync<List<HarvestedDocument>>(stream, ReadOptions, cancellationToken);
        return docs is not null && pos >= 0 && pos < docs.Count ? docs[pos] : null;
    }
}

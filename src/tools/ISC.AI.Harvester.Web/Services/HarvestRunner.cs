using System.Globalization;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Harvester.Web.Services;

/// <summary>Состояние фонового сбора.</summary>
public enum HarvestStatus
{
    /// <summary>Не запускался.</summary>
    Idle,

    /// <summary>Идёт сбор.</summary>
    Running,

    /// <summary>Остановлен оператором (частичный пакет сохранён).</summary>
    Stopped,

    /// <summary>Завершён (источник исчерпан или достигнут лимит).</summary>
    Completed,

    /// <summary>Прерван ошибкой.</summary>
    Failed,
}

/// <summary>Лёгкая строка результата для UI (без полного текста — экономия памяти на 170К, Э4-17).</summary>
/// <param name="Index">Глобальный индекс в пакете (для чтения текста по требованию).</param>
/// <param name="Title">Заголовок.</param>
/// <param name="SourceUrl">Ссылка на источник.</param>
/// <param name="Status">Статус НПА (напр. «Действует»).</param>
/// <param name="TextLength">Длина извлечённого текста.</param>
public sealed record HarvestRow(int Index, string Title, string SourceUrl, string Status, int TextLength)
{
    /// <summary>Подозрительно короткий текст (вероятно, пусто/мусор) — для подсветки в UI.</summary>
    public bool IsShort => TextLength < ShortTextThreshold;

    internal const int ShortTextThreshold = 300;
}

/// <summary>
/// Фоновый оркестратор массового сбора (Э4-17). Запускает коннектор, СТРИМИТ документы в шардированный
/// пакет (память ограничена), держит только лёгкие строки для UI, публикует прогресс и поддерживает Стоп.
/// Синглтон: один сбор за раз; переживает уход оператора со страницы (сбор идёт в фоне).
/// </summary>
public sealed class HarvestRunner(IServiceScopeFactory scopeFactory) : IDisposable
{
    internal const int ShardSize = 2000;

    private readonly object _lock = new();
    private readonly List<HarvestRow> _rows = [];
    private CancellationTokenSource? _cts;

    /// <summary>Текущее состояние.</summary>
    public HarvestStatus Status { get; private set; } = HarvestStatus.Idle;

    /// <summary>Собрано документов.</summary>
    public int Count { get; private set; }

    /// <summary>Сколько собрано с подозрительно коротким текстом (вероятно пусто/мусор).</summary>
    public int ShortCount { get; private set; }

    /// <summary>Последняя обработанная страница источника (чекпоинт для возобновления).</summary>
    public int CurrentPage { get; private set; }

    /// <summary>Каталог текущего/последнего пакета.</summary>
    public string? BundleDir { get; private set; }

    /// <summary>Путь к manifest.json готового пакета.</summary>
    public string? ManifestPath { get; private set; }

    /// <summary>Текст ошибки (если <see cref="Status"/> = Failed).</summary>
    public string? Error { get; private set; }

    /// <summary>Момент старта.</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>Идёт ли сбор.</summary>
    public bool IsRunning => Status == HarvestStatus.Running;

    /// <summary>Событие изменения состояния (UI подписывается и перерисовывается).</summary>
    public event Action? Changed;

    /// <summary>Страница результатов: фильтр по заголовку + срез (для <c>MudTable</c> в режиме ServerData).</summary>
    public (IReadOnlyList<HarvestRow> Page, int Filtered, int Total) GetPage(string? search, int skip, int take)
    {
        lock (_lock)
        {
            IEnumerable<HarvestRow> query = _rows;
            if (!string.IsNullOrWhiteSpace(search))
            {
                query = _rows.Where(r => r.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            var filtered = query.ToList();
            var page = filtered.Skip(skip).Take(take).ToList();
            return (page, filtered.Count, _rows.Count);
        }
    }

    /// <summary>Читает полный текст документа по индексу (из шарда, по требованию — для просмотра в UI).</summary>
    public Task<HarvestedDocument?> ReadDocumentAsync(int index) =>
        BundleDir is null
            ? Task.FromResult<HarvestedDocument?>(null)
            : StreamingBundleWriter.ReadDocumentAsync(BundleDir, index, ShardSize);

    /// <summary>Запускает фоновый сбор (не блокирует UI). <paramref name="config"/>.StartPage — для возобновления.</summary>
    public void Start(SourceConfig config, string connectorId)
    {
        if (Status == HarvestStatus.Running)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        lock (_lock)
        {
            _rows.Clear();
        }

        Count = 0;
        ShortCount = 0;
        CurrentPage = 0;
        Error = null;
        ManifestPath = null;
        BundleDir = null;
        StartedAt = DateTimeOffset.Now;
        Status = HarvestStatus.Running;
        Raise();

        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(config, connectorId, token));
    }

    /// <summary>Останавливает сбор (частичный пакет финализируется и остаётся импортируемым).</summary>
    public void Stop() => _cts?.Cancel();

    private async Task RunAsync(SourceConfig config, string connectorId, CancellationToken token)
    {
        var writer = new StreamingBundleWriter(ShardSize);
        var directory = Path.Combine(
            AppContext.BaseDirectory, "bundles",
            "bundle-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        try
        {
            using var scope = scopeFactory.CreateScope();
            var connector = scope.ServiceProvider.GetServices<ISourceConnector>().First(c => c.Id == connectorId);
            writer.Begin(directory);
            BundleDir = directory;

            await foreach (var document in connector.HarvestAsync(config, token))
            {
                await writer.AppendAsync(document, token);

                var status = document.Metadata is not null && document.Metadata.TryGetValue("status", out var s) ? s : string.Empty;
                var row = new HarvestRow(Count, document.Title, document.SourceUrl, status, document.Text.Length);
                lock (_lock)
                {
                    _rows.Add(row);
                }

                Count++;
                if (row.IsShort)
                {
                    ShortCount++;
                }
                if (document.Metadata is not null && document.Metadata.TryGetValue("page", out var p)
                    && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
                {
                    CurrentPage = page;
                }

                if (Count % 25 == 0)
                {
                    Raise();
                }
            }

            ManifestPath = await writer.CompleteAsync(CancellationToken.None);
            Status = HarvestStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            await FinalizeQuietlyAsync(writer);
            Status = HarvestStatus.Stopped;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            await FinalizeQuietlyAsync(writer);
            Status = HarvestStatus.Failed;
        }
        finally
        {
            Raise();
        }
    }

    // Финализируем частичный пакет (индекс уже записанных шардов), чтобы стоп/ошибка не теряли собранное.
    private async Task FinalizeQuietlyAsync(StreamingBundleWriter writer)
    {
        try
        {
            ManifestPath = await writer.CompleteAsync(CancellationToken.None);
        }
        catch
        {
            // пакет мог не начаться — игнорируем
        }
    }

    private void Raise() => Changed?.Invoke();

    /// <inheritdoc />
    public void Dispose() => _cts?.Dispose();
}

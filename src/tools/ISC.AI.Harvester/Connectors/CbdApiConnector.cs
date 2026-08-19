using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Engine;

namespace ISC.AI.Harvester.Connectors;

/// <summary>
/// Коннектор к ОФИЦИАЛЬНОМУ API ЦБД Минюста КР (Э4-16). В отличие от скрапинга SPA (который тела акта не
/// отдаёт), берёт данные из REST: список — <c>POST /api/v1/GetDocuments</c> (со статусом), текст —
/// <c>GET /api/v1/GetEdition</c> (<c>contentRu</c> — полный текст акта). Обычный HTTP, без браузера.
/// </summary>
/// <remarks>
/// Только вне контура (инструмент подготовки данных, air-gap не нарушается). В корпус берутся ТОЛЬКО
/// действующие акты (whitelist по <c>status</c>, fail-closed): всё, что не «Действует», отсеивается — так
/// в контур не попадут утратившие силу/приостановленные редакции (опора ТО-инф-04, КИ-02). Гриф/подразделение —
/// из конфига оператора (открытые материалы).
/// </remarks>
public sealed class CbdApiConnector(HttpClient httpClient, IContentExtractor extractor) : ISourceConnector
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Единственный «действующий» статус — whitelist (прочие статусы = не выдаём как действующие).</summary>
    private const string ActiveStatus = "Действует";
    private const int PageSize = 50;
    private const int MaxPages = 50_000; // предохранитель от бесконечного обхода (при пустой странице — стоп раньше)

    /// <inheritdoc />
    public string Id => "cbd-api";

    /// <inheritdoc />
    public string DisplayName => "ЦБД Минюста — официальный API (только действующие)";

    /// <inheritdoc />
    public async IAsyncEnumerable<HarvestedDocument> HarvestAsync(
        SourceConfig config, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var origin = Origin(config.SeedUrl);

        var collected = 0;
        for (var page = Math.Max(1, config.StartPage); page <= MaxPages && collected < config.MaxDocuments; page++)
        {
            var items = await GetDocumentsPageAsync(origin, page, cancellationToken);
            if (items.Count == 0)
            {
                yield break; // страницы кончились
            }

            foreach (var item in items)
            {
                if (collected >= config.MaxDocuments)
                {
                    yield break;
                }

                // Только действующие (fail-closed): не «Действует» или нет редакции — пропускаем.
                if (!string.Equals(item.Status, ActiveStatus, StringComparison.Ordinal) || item.LastEdition <= 0)
                {
                    continue;
                }

                var text = await GetEditionTextAsync(origin, item.LastEdition, cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                collected++;
                yield return new HarvestedDocument(
                    SourceUrl: $"{origin}/{item.DocumentCode}/edition/{item.LastEdition}/ru",
                    Title: (item.NameRu ?? string.Empty).Trim(),
                    Text: text,
                    DocType: string.IsNullOrWhiteSpace(item.Vid) ? config.DocType : item.Vid!,
                    ContentHash: Hash(text),
                    Classification: config.Classification,
                    DivisionId: null, // выбирается при импорте внутри контура (SourceConfig)
                    Language: "ru",
                    Metadata: new Dictionary<string, string>
                    {
                        ["status"] = item.Status ?? string.Empty,
                        ["editionId"] = item.LastEdition.ToString(CultureInfo.InvariantCulture),
                        ["documentCode"] = item.DocumentCode ?? string.Empty,
                        ["page"] = page.ToString(CultureInfo.InvariantCulture), // для чекпоинта возобновления (Э4-17)
                    });
            }
        }
    }

    private async Task<IReadOnlyList<DocItem>> GetDocumentsPageAsync(string origin, int page, CancellationToken ct)
    {
        var url = $"{origin}/api/v1/GetDocuments?pageNumber={page}&pageSize={PageSize}";
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await httpClient.PostAsync(url, body, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var parsed = await JsonSerializer.DeserializeAsync<GetDocumentsResponse>(stream, JsonOpts, ct);
        return parsed?.Data ?? [];
    }

    private async Task<string> GetEditionTextAsync(string origin, int editionId, CancellationToken ct)
    {
        var url = $"{origin}/api/v1/GetEdition?editionId={editionId.ToString(CultureInfo.InvariantCulture)}&lang=ru&exact=false";
        using var resp = await httpClient.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return string.Empty;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var edition = await JsonSerializer.DeserializeAsync<GetEditionResponse>(stream, JsonOpts, ct);
        var html = edition?.ContentRu ?? string.Empty;
        // contentRu — экспорт из Word (HTML со стилями); извлекатель снимает style/script и даёт чистый текст.
        return string.IsNullOrWhiteSpace(html) ? string.Empty : extractor.Extract(html, url).Text;
    }

    private static string Origin(string seedUrl) =>
        Uri.TryCreate(seedUrl, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : "https://cbd.minjust.gov.kg";

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class GetDocumentsResponse
    {
        public List<DocItem>? Data { get; set; }
    }

    private sealed class DocItem
    {
        public string? DocumentCode { get; set; }
        public string? NameRu { get; set; }
        public string? Status { get; set; }
        public string? Vid { get; set; }
        public int LastEdition { get; set; }
    }

    private sealed class GetEditionResponse
    {
        public string? ContentRu { get; set; }
        public string? ContentKg { get; set; }
    }
}

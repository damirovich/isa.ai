using ISC.AI.AI.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ISC.AI.UnitTests.Retrieval;

/// <summary>
/// ТО-мат-04: ранжирование И порог отсечения задаются КОНФИГУРАЦИЕЙ (секция <c>Retrieval</c>).
/// Проверяем, что <see cref="RetrievalOptions"/> собирается из конфигурации, а не хардкодится.
/// Отдельно — <see cref="RetrievalOptions.HnswEfSearch"/> (ТБ-022): дефолт 200 обязателен, потому
/// что дефолт pgvector (40) доказанно пропускает малые семантические «острова» (инцидент
/// 26.08.2026, см. docs/reference/pgvector-hnsw-recall.md).
/// </summary>
public sealed class RetrievalOptionsConfigTests
{
    private static RetrievalOptions Resolve(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        using var provider = new ServiceCollection().AddCoreRetrieval(configuration).BuildServiceProvider();
        return provider.GetRequiredService<RetrievalOptions>();
    }

    [Fact(DisplayName = "ТО-мат-04: порог и метрика читаются из конфигурации")]
    public void Threshold_and_metric_are_read_from_configuration()
    {
        var options = Resolve(("Retrieval:MaxDistance", "0.35"), ("Retrieval:Metric", "Euclidean"));

        options.MaxDistance.ShouldBe(0.35);
        options.Metric.ShouldBe(RetrievalMetric.Euclidean);
    }

    [Fact(DisplayName = "ТО-мат-04: без конфигурации — безопасные дефолты (без отсечения, метрика Cosine)")]
    public void Missing_configuration_yields_safe_defaults()
    {
        var options = Resolve();

        options.MaxDistance.ShouldBeNull();
        options.Metric.ShouldBe(RetrievalMetric.Cosine);
    }

    [Fact(DisplayName = "ТО-мат-04: нераспознанная метрика → Cosine (совпадает с HNSW-индексом)")]
    public void Unknown_metric_falls_back_to_cosine()
    {
        Resolve(("Retrieval:Metric", "чепуха")).Metric.ShouldBe(RetrievalMetric.Cosine);
    }

    [Fact(DisplayName = "ТБ-022: без конфигурации hnsw.ef_search = 200 — а не pgvector-дефолт 40")]
    public void Default_ef_search_is_island_safe()
    {
        Resolve().HnswEfSearch.ShouldBe(200);
    }

    [Fact(DisplayName = "ТБ-022: Retrieval:HnswEfSearch из конфигурации применяется")]
    public void Configured_ef_search_is_used()
    {
        Resolve(("Retrieval:HnswEfSearch", "400")).HnswEfSearch.ShouldBe(400);
    }

    [Fact(DisplayName = "ТБ-022: мусор в Retrieval:HnswEfSearch не роняет запуск — берётся дефолт 200")]
    public void Garbage_ef_search_falls_back_to_default()
    {
        Resolve(("Retrieval:HnswEfSearch", "много")).HnswEfSearch.ShouldBe(200);
    }
}

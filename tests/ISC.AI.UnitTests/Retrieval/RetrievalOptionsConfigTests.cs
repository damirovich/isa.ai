using ISC.AI.AI.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ISC.AI.UnitTests.Retrieval;

/// <summary>
/// ТО-мат-04: ранжирование И порог отсечения задаются КОНФИГУРАЦИЕЙ (секция <c>Retrieval</c>).
/// Проверяем, что <see cref="RetrievalOptions"/> собирается из конфигурации, а не хардкодится.
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
}

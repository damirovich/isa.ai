using ISC.AI.AI.Grounding;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Ingestion;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Grounding;
using ISC.AI.Profile.Inspector.Application.Features.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles.Grounding;

/// <summary>
/// Композиция грунтовки (Э4-18): профиль ПЕРЕОПРЕДЕЛЯЕТ нейтральные заглушки ядра. Порядок как в хосте —
/// сначала ядро (<c>AddCoreGrounding</c>/<c>AddCoreIngestion</c>), затем профиль (<c>AddInspectorApplication</c>).
/// Guard: если override сломается (напр. TryAdd вместо Replace или обратный порядок), грунтовка молча
/// деградирует в no-op (<c>NoCitationExtractor</c>) — этот тест это ловит.
/// </summary>
public sealed class GroundingCompositionTests
{
    [Fact(DisplayName = "Композиция: профиль переопределяет экстрактор/нормализатор/чанкер ядра на НПА-реализации")]
    public void Profile_overrides_core_grounding_seams()
    {
        var services = new ServiceCollection();
        services.AddCoreGrounding();
        services.AddCoreIngestion();
        services.AddInspectorApplication();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ICitationExtractor>().ShouldBeOfType<NpaCitationExtractor>();
        provider.GetRequiredService<ICitationNormalizer>().ShouldBeOfType<NpaCitationNormalizer>();
        provider.GetRequiredService<ITextChunker>().ShouldBeOfType<NpaStructuralChunker>();
    }

    [Fact(DisplayName = "Инвариант №1: профиль НЕ подменяет сам валидатор грунтовки ядра (ТБ-041)")]
    public void Profile_does_not_replace_core_grounding_validator()
    {
        // Профиль может переопределить доменные extractor/normalizer, но НЕ сам инвариант грунтовки.
        // Регресс (профиль случайно Replace(IGroundingValidator)) должен ловиться сборкой, а не ревью.
        var services = new ServiceCollection();
        services.AddCoreGrounding();
        services.AddCoreIngestion();
        services.AddInspectorApplication();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IGroundingValidator>().ShouldBeOfType<GroundingValidator>();
    }
}

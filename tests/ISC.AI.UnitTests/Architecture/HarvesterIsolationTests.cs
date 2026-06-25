using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Architecture;

/// <summary>
/// Режимная изоляция сборщика (Э4-08, ADR-0015): он вне контура и НЕ должен ссылаться на проекты
/// контура (Persistence/AI/Web/Ingestion/Documents/профиль) и на драйверы БД — единственный путь
/// данных в контур — пакет импорта.
/// </summary>
public sealed class HarvesterIsolationTests
{
    [Fact(DisplayName = "Harvester вне контура: не ссылается на проекты контура и драйверы БД (air-gap)")]
    public void Harvester_does_not_reference_contour()
    {
        var referenced = typeof(ISourceConnector).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name ?? string.Empty)
            .ToList();

        string[] forbidden =
        [
            "ISC.AI.Persistence", "ISC.AI.AI", "ISC.AI.Web", "ISC.AI.Ingestion", "ISC.AI.Documents",
            "Npgsql", "Microsoft.EntityFrameworkCore",
        ];

        referenced.ShouldNotContain(name =>
            forbidden.Contains(name) || name.StartsWith("ISC.AI.Profile", StringComparison.Ordinal));
    }
}

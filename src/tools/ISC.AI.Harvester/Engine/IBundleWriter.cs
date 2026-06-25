using ISC.AI.Abstractions.Harvesting;

namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Запись пакета импорта (ADR-0015): собранные документы → переносимый артефакт для контура.
/// Пакет — единственное, что пересекает зазор; его читает импортёр в контуре.
/// </summary>
public interface IBundleWriter
{
    /// <summary>Записывает документы в каталог пакета и возвращает путь к манифесту.</summary>
    Task<string> WriteAsync(
        string outputDirectory,
        IReadOnlyCollection<HarvestedDocument> documents,
        CancellationToken cancellationToken = default);
}

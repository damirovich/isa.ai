using ISC.AI.Abstractions.Harvesting;

namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Подключаемый источник сбора (ADR-0015): «движок-агностик + коннекторы». Коннектор сам обходит/качает
/// источник и выдаёт нормализованные <see cref="HarvestedDocument"/>. Generic-коннектор работает на
/// любом URL (best-effort); адаптеры (gov.kg и т. д.) дают точные метаданные. Новый источник = новый коннектор.
/// </summary>
public interface ISourceConnector
{
    /// <summary>Идентификатор коннектора (например, «generic-url», «gov.kg»).</summary>
    string Id { get; }

    /// <summary>Человекочитаемое имя для UI оператора.</summary>
    string DisplayName { get; }

    /// <summary>Собирает документы из источника (вежливо: лимит, троттлинг — на стороне реализации).</summary>
    IAsyncEnumerable<HarvestedDocument> HarvestAsync(SourceConfig config, CancellationToken cancellationToken = default);
}

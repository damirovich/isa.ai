using ISC.AI.Abstractions.Harvesting;

namespace ISC.AI.Abstractions.Ingestion;

/// <summary>
/// Импорт пакета, собранного вне контура (ADR-0015, Э4-08↔Э4-01): читает <c>manifest.json</c>
/// (массив <see cref="HarvestedDocument"/>) и загружает каждый документ через <see cref="IIngestionPort"/>
/// (fail-closed гриф, дедуп — в порту). Это В-КОНТУРНАЯ сторона: интернета не касается.
/// </summary>
public interface IBundleImporter
{
    /// <summary>
    /// Импортирует документы из манифеста пакета в корпус. <paramref name="divisionId"/> — подразделение,
    /// ПЕРЕКРЫВАЮЩЕЕ записанное в пакете: пакет собран вне контура, где справочника подразделений нет,
    /// и записанный там номер — лишь намерение сборщика; решение о том, чьим числится материал, принимает
    /// оператор внутри контура, видя справочник. <see langword="null"/> — довериться пакету (как было).
    /// <paramref name="classification"/> — гриф, ПОДТВЕРЖДЁННЫЙ оператором (ТБ-024), перекрывает
    /// записанный в пакете по той же причине: пакет — от недоверенного производителя вне контура.
    /// <see langword="null"/> — довериться пакету (fail-closed по отсутствию грифа остаётся в порту).
    /// </summary>
    Task<BundleImportResult> ImportAsync(
        string manifestPath, int? divisionId = null, short? classification = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Итог импорта пакета.</summary>
/// <param name="Total">Всего документов в манифесте.</param>
/// <param name="Imported">Проиндексировано новых.</param>
/// <param name="Duplicates">Пропущено дубликатов (уже в корпусе).</param>
/// <param name="Rejected">Отклонено (например, fail-closed по грифу).</param>
public sealed record BundleImportResult(int Total, int Imported, int Duplicates, int Rejected);

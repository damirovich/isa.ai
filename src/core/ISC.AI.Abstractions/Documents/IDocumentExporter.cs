namespace ISC.AI.Abstractions.Documents;

/// <summary>
/// Экспорт документа в офисный формат (ТФ-ГЕН-03). Нейтрален к типу документа; обязательную маркировку
/// грифа (ТБ-033) проставляет реализация — в теле и в метаданных файла. Сам факт экспорта аудирует
/// вызывающий сценарий (ТБ-030, <see cref="ISC.AI.Abstractions.Audit.AuditAction.Export"/>).
/// </summary>
public interface IDocumentExporter
{
    /// <summary>Рендерит документ в <c>.docx</c> и возвращает его байты.</summary>
    Task<byte[]> ExportToDocxAsync(DocumentExportRequest request, CancellationToken cancellationToken = default);
}

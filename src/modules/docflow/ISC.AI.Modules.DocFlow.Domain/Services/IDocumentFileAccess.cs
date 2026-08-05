namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Файл, найденный по маршруту раздачи (категория + идентификатор родителя + имя в хранилище),
/// с режимными метаданными ВЛАДЕЮЩЕГО документа — основа проверки допуска перед стримом (ТБ-020/021).
/// </summary>
public sealed record ResolvedFile(
    string StoredFileName, string SubPath, string ContentType, string FileName,
    short Classification, int DivisionId, bool IsPdfCopy);

/// <summary>
/// Резолвинг файла по маршруту раздачи (этап 4.3 Э4-35, §3.3): связывает публичный URL
/// (категория/id родителя/имя файла) со строкой в БД и режимными метаданными документа, которому файл
/// принадлежит — ТОЛЬКО ЧТЕНИЕ. Реализация — в слое данных (все четыре файловые таблицы + Document).
/// </summary>
public interface IDocumentFileAccess
{
    /// <summary>
    /// Ищет файл в категории <paramref name="category"/> (см. <see cref="FileCategories"/>) у родителя
    /// <paramref name="parentId"/>; несовпадение родителя с фактическим — тоже <see langword="null"/>
    /// (заявленный маршрут не подтверждён, а не «файл существует, но чужой» — не течёт наружу).
    /// </summary>
    Task<ResolvedFile?> ResolveAsync(
        string category, int parentId, string storedFileName, CancellationToken cancellationToken = default);
}

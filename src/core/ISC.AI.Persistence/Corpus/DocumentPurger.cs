using System.Globalization;
using ISC.AI.Abstractions.Corpus;

namespace ISC.AI.Persistence.Corpus;

/// <summary>
/// Гарантированное удаление документа и всех производных из схемы <c>core</c> (ТБ-064). Физическое
/// удаление (не мягкое): документ/чанк/эмбеддинг намеренно НЕ <c>ISoftDeletable</c>. Чанки и эмбеддинги
/// (pgvector) снимаются каскадом БД (<c>ON DELETE CASCADE</c>); задания индексации ссылаются на документ
/// без FK и удаляются явно; «висячие» ссылки-преемники у других документов снимаются.
/// </summary>
/// <remarks>
/// Порядок FAIL-CLOSED (ТБ-064): запись в неизменяемый аудит идёт ПЕРВОЙ — если журнал недоступен,
/// исключение прерывает операцию и удаление не выполняется (нет уничтожения ДСП без записи в журнал).
/// Аудит ведёт собственную транзакцию (advisory-lock хеш-цепочки), поэтому в общую транзакцию удаления
/// он не входит; само удаление атомарно в своей транзакции — частичный результат недопустим.
/// </remarks>
public sealed class DocumentPurger(
    IDbContextFactory<CoreDbContext> contextFactory,
    IAuditWriter auditWriter) : IDocumentPurger
{
    /// <inheritdoc />
    public async Task<DocumentPurgeResult> PurgeAsync(
        int documentId, int? subjectId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Метаданные нужны для аудита (гриф обязателен) и для возврата ссылки на файл-исходник.
        var document = await db.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Classification, d.DivisionId, d.StorageUri })
            .FirstOrDefaultAsync(cancellationToken);

        if (document is null)
        {
            return DocumentPurgeResult.NotFound; // идемпотентно: удалять нечего, аудит не пишем
        }

        // FAIL-CLOSED (ТБ-064): фиксируем удаление в неизменяемом журнале ДО уничтожения данных. Если
        // аудит недоступен — WriteAsync бросит исключение и удаление НЕ произойдёт.
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Purge,
                document.Classification,
                SubjectId: subjectId,
                ObjectRef: documentId.ToString(CultureInfo.InvariantCulture),
                DivisionId: document.DivisionId,
                PayloadSensitive: "Гарантированное удаление документа и всех производных (ТБ-064)."),
            cancellationToken);

        // Атомарно: частичное удаление недопустимо (иначе часть ДСП может остаться).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Снять «висячие» ссылки-преемники у ДРУГИХ документов, указывавших на удаляемый (слабая ссылка
        // по значению в пределах схемы core, без FK — Э4-14).
        await db.Documents
            .Where(d => d.SupersededByDocumentId == documentId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.SupersededByDocumentId, (int?)null), cancellationToken);

        // Задания индексации ссылаются на документ БЕЗ FK (каскада нет) — удаляем явно.
        await db.IndexingJobs
            .Where(j => j.DocumentId == documentId)
            .ExecuteDeleteAsync(cancellationToken);

        // Документ: чанки и эмбеддинги (pgvector) снимаются каскадом БД (ON DELETE CASCADE, см. конфигурации).
        await db.Documents
            .Where(d => d.Id == documentId)
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new DocumentPurgeResult(Found: true, StorageUri: document.StorageUri);
    }
}

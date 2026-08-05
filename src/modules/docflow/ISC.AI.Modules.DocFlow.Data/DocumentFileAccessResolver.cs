using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Резолвинг файла по маршруту раздачи (этап 4.3 Э4-35) поверх <see cref="DocFlowDbContext"/>.
/// </summary>
/// <remarks>
/// Запросы — явными пошаговыми поисками по id (без навигаций «файл → … → документ»): доменные
/// сущности файлов намеренно тонкие (FK-поле, без сквозных навигаций ради чтения), а несовпадение
/// <paramref name="parentId"/>-маршрута с фактическим родителем — часть проверки (см. интерфейс).
/// </remarks>
public sealed class DocumentFileAccessResolver(IDbContextFactory<DocFlowDbContext> contextFactory)
    : IDocumentFileAccess
{
    /// <inheritdoc />
    public async Task<ResolvedFile?> ResolveAsync(
        string category, int parentId, string storedFileName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return category switch
        {
            FileCategories.Documents => await ResolveDocumentFileAsync(db, parentId, storedFileName, cancellationToken),
            FileCategories.Attachments => await ResolveAttachmentAsync(db, parentId, storedFileName, cancellationToken),
            FileCategories.StatusHistory => await ResolveStatusHistoryFileAsync(db, parentId, storedFileName, cancellationToken),
            FileCategories.DeadlineExtensions => await ResolveDeadlineExtensionFileAsync(db, parentId, storedFileName, cancellationToken),
            _ => null,
        };
    }

    private static async Task<ResolvedFile?> ResolveDocumentFileAsync(
        DocFlowDbContext db, int documentId, string storedFileName, CancellationToken cancellationToken)
    {
        var row = await db.DocumentFiles.AsNoTracking()
            .Where(f => f.DocumentId == documentId
                && (f.StoredFileName == storedFileName || f.PdfCopyStoredFileName == storedFileName))
            .Select(f => new { f.StoredFileName, f.PdfCopyStoredFileName, f.ContentType, f.FileName, f.DocumentId })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var access = await ResolveDocumentAccessAsync(db, row.DocumentId, cancellationToken);
        if (access is null)
        {
            return null;
        }

        var isPdfCopy = string.Equals(row.PdfCopyStoredFileName, storedFileName, StringComparison.Ordinal);
        return new ResolvedFile(
            storedFileName, SubPath(row.DocumentId),
            isPdfCopy ? "application/pdf" : row.ContentType,
            isPdfCopy ? Path.ChangeExtension(row.FileName, ".pdf") : row.FileName,
            access.Value.Classification, access.Value.DivisionId, isPdfCopy);
    }

    private static async Task<ResolvedFile?> ResolveAttachmentAsync(
        DocFlowDbContext db, int documentId, string storedFileName, CancellationToken cancellationToken)
    {
        var row = await db.DocumentAttachments.AsNoTracking()
            .Where(a => a.DocumentId == documentId && a.StoredFileName == storedFileName)
            .Select(a => new { a.ContentType, a.FileName, a.DocumentId })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var access = await ResolveDocumentAccessAsync(db, row.DocumentId, cancellationToken);
        if (access is null)
        {
            return null;
        }

        return new ResolvedFile(
            storedFileName, SubPath(row.DocumentId), row.ContentType, row.FileName,
            access.Value.Classification, access.Value.DivisionId, IsPdfCopy: false);
    }

    private static async Task<ResolvedFile?> ResolveStatusHistoryFileAsync(
        DocFlowDbContext db, int assignmentId, string storedFileName, CancellationToken cancellationToken)
    {
        var fileRow = await db.StatusHistoryFiles.AsNoTracking()
            .Where(f => f.StoredFileName == storedFileName)
            .Select(f => new { f.ContentType, f.FileName, f.StatusHistoryId })
            .FirstOrDefaultAsync(cancellationToken);
        if (fileRow is null)
        {
            return null;
        }

        // Родитель из маршрута обязан совпасть с фактическим (переход принадлежит ЭТОМУ назначению) —
        // несовпадение не отличается наружу от «файла нет» (не подтверждаем чужой файл существующим).
        var documentId = await db.AssignmentStatusHistories.AsNoTracking()
            .Where(h => h.Id == fileRow.StatusHistoryId && h.AssignmentId == assignmentId)
            .Join(db.DocumentAssignments, h => h.AssignmentId, a => a.Id, (h, a) => a.DocumentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (documentId == 0)
        {
            return null;
        }

        var access = await ResolveDocumentAccessAsync(db, documentId, cancellationToken);
        if (access is null)
        {
            return null;
        }

        return new ResolvedFile(
            storedFileName, SubPath(assignmentId), fileRow.ContentType, fileRow.FileName,
            access.Value.Classification, access.Value.DivisionId, IsPdfCopy: false);
    }

    private static async Task<ResolvedFile?> ResolveDeadlineExtensionFileAsync(
        DocFlowDbContext db, int assignmentId, string storedFileName, CancellationToken cancellationToken)
    {
        var fileRow = await db.DeadlineExtensionFiles.AsNoTracking()
            .Where(f => f.StoredFileName == storedFileName)
            .Select(f => new { f.ContentType, f.FileName, f.ExtensionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (fileRow is null)
        {
            return null;
        }

        var documentId = await db.DeadlineExtensions.AsNoTracking()
            .Where(e => e.Id == fileRow.ExtensionId && e.AssignmentId == assignmentId)
            .Join(db.DocumentAssignments, e => e.AssignmentId, a => a.Id, (e, a) => a.DocumentId)
            .FirstOrDefaultAsync(cancellationToken);
        if (documentId == 0)
        {
            return null;
        }

        var access = await ResolveDocumentAccessAsync(db, documentId, cancellationToken);
        if (access is null)
        {
            return null;
        }

        return new ResolvedFile(
            storedFileName, SubPath(assignmentId), fileRow.ContentType, fileRow.FileName,
            access.Value.Classification, access.Value.DivisionId, IsPdfCopy: false);
    }

    private static async Task<(short Classification, int DivisionId)?> ResolveDocumentAccessAsync(
        DocFlowDbContext db, int documentId, CancellationToken cancellationToken)
    {
        var row = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Classification, d.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : (row.Classification, row.DivisionId);
    }

    // Единая схема подпути хранилища (совпадает с DocumentStore при сохранении, этап 4.2): один
    // сегмент — id родителя (документа либо назначения).
    private static string SubPath(int parentId) =>
        parentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

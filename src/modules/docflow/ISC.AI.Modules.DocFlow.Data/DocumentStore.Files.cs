using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Файловая часть хранилища (§3.3): версионируемый файл документа и сопутствующие вложения.
/// Partial-файл выделен ПО ТЕМЕ из монолитного DocumentStore (рефакторинг 2026-08-10):
/// класс один — предикат видимости и правила записи общие, но читать 1300 строк одной простынёй
/// нельзя было уже никому.
/// </summary>
public sealed partial class DocumentStore
{
    /// <inheritdoc />
    public async Task<DocumentWriteStatus> AddDocumentFileAsync(
        int documentId, UploadedFile file, DocumentLanguage language, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь (WriteAccessRule): недоступный документ — тот же NotFound.
        if (!await IsDocumentVisibleAsync(db, documentId, access, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
        }

        var uploadedByUserId = access.NumericSubjectId;

        // Содержимое — в хранилище ДО записи в БД; при сбое БД файл компенсирующе удаляется (как в СКИД).
        var subPath = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var content = new MemoryStream(file.Content);
        var storedFileName = await fileStorage.SaveAsync(
            content, Path.GetExtension(file.FileName), FileCategories.Documents, subPath, cancellationToken);

        try
        {
            // §3.3: замена файла создаёт новую версию; прежняя того же языка теряет актуальность.
            var latest = await db.DocumentFiles.FirstOrDefaultAsync(
                f => f.DocumentId == documentId && f.Language == language && f.IsLatest, cancellationToken);
            if (latest is not null)
            {
                latest.IsLatest = false;
            }

            var maxVersion = await db.DocumentFiles
                .Where(f => f.DocumentId == documentId && f.Language == language)
                .Select(f => (int?)f.Version)
                .MaxAsync(cancellationToken) ?? 0;

            db.DocumentFiles.Add(new DocumentFile
            {
                DocumentId = documentId,
                Language = language,
                FileName = file.FileName,
                StoredFileName = storedFileName,
                ContentType = file.ContentType,
                FileSize = file.Content.LongLength,
                Version = maxVersion + 1,
                IsLatest = true,
                UploadedByUserId = uploadedByUserId ?? 0,
            });
            await db.SaveChangesAsync(cancellationToken);
            return DocumentWriteStatus.Ok;
        }
        catch
        {
            await fileStorage.DeleteAsync(storedFileName, FileCategories.Documents, subPath, CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DocumentWriteStatus> AddAttachmentAsync(
        int documentId, UploadedFile file, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь (WriteAccessRule).
        if (!await IsDocumentVisibleAsync(db, documentId, access, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
        }

        var uploadedByUserId = access.NumericSubjectId;

        // Лимит — ДО записи в хранилище (не тратим байты на диске, если операция всё равно отклонится).
        var attachmentCount = await db.DocumentAttachments
            .CountAsync(a => a.DocumentId == documentId, cancellationToken);
        if (attachmentCount >= MaxAttachmentsPerDocument)
        {
            return DocumentWriteStatus.TooManyAttachments;
        }

        var subPath = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var content = new MemoryStream(file.Content);
        var storedFileName = await fileStorage.SaveAsync(
            content, Path.GetExtension(file.FileName), FileCategories.Attachments, subPath, cancellationToken);

        try
        {
            db.DocumentAttachments.Add(new DocumentAttachment
            {
                DocumentId = documentId,
                FileName = file.FileName,
                StoredFileName = storedFileName,
                ContentType = file.ContentType,
                FileSize = file.Content.LongLength,
                UploadedByUserId = uploadedByUserId ?? 0,
            });
            await db.SaveChangesAsync(cancellationToken);
            return DocumentWriteStatus.Ok;
        }
        catch
        {
            await fileStorage.DeleteAsync(storedFileName, FileCategories.Attachments, subPath, CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DocumentWriteStatus> DeleteAttachmentAsync(
        int attachmentId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var attachment = await db.DocumentAttachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
        if (attachment is null)
        {
            return DocumentWriteStatus.NotFound;
        }

        // Нельзя менять то, чего не видишь (WriteAccessRule): недоступный документ — тот же NotFound,
        // и по ответу нельзя узнать, существует ли вложение.
        if (!await IsDocumentVisibleAsync(db, attachment.DocumentId, access, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
        }

        var storedFileName = attachment.StoredFileName;
        var subPath = attachment.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        db.DocumentAttachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);

        // Файл с диска — ПОСЛЕ успешной записи в БД. Обратный порядок при сбое сохранения оставил бы
        // строку, указывающую в пустоту; здесь же худший исход — осиротевший файл, который не виден
        // ниоткуда и не мешает работе.
        try
        {
            await fileStorage.DeleteAsync(
                storedFileName, FileCategories.Attachments, subPath, CancellationToken.None);
        }
        catch (IOException)
        {
            // Файл занят или уже удалён — запись в БД снята, для пользователя вложения больше нет.
        }

        return DocumentWriteStatus.Ok;
    }
}

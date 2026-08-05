using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Индексатор документов документооборота в корпус ядра (этап 7 Э4-35, ADR-0017 п.6) — первый
/// прикладной потребитель конвейера загрузки вне страницы «Загрузка корпуса».
/// </summary>
/// <remarks>
/// Механика: текст (краткое содержание + полный текст) → <see cref="IIngestionPort"/> с грифом и
/// подразделением ДОКУМЕНТА (fail-closed ТБ-024 соблюдён по построению — поля NOT NULL);
/// при переиндексации прежний корпусный документ гасится supersede-механикой ядра (Э4-14);
/// связь ведёт мостик <c>document_index_link</c> (одна строка на документ). Идентичный текст
/// дедуплицируется ядром по хешу (ТНД-002) — статус <see cref="DocumentIndexStatus.Unchanged"/>.
/// Успешная индексация аудируется (<see cref="AuditAction.Ingest"/>, гриф записи — гриф документа).
/// </remarks>
public sealed class DocFlowDocumentIndexer(
    IDbContextFactory<DocFlowDbContext> docFlowFactory,
    IDbContextFactory<CoreDbContext> coreFactory,
    IIngestionPort ingestionPort,
    IAuditWriter auditWriter) : IDocumentIndexer
{
    /// <inheritdoc />
    public async Task<DocumentIndexResult> IndexAsync(int documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await docFlowFactory.CreateDbContextAsync(cancellationToken);

        var document = await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.Id, d.RegNumber, d.RegDate, d.ShortContent, d.FullText, d.Classification, d.DivisionId, TypeName = d.Type!.Name, d.Type!.Group })
            .FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return new DocumentIndexResult(DocumentIndexStatus.NotFound);
        }

        // Мостик: есть ли уже корпусная версия. Осиротевшая ссылка (корпусный документ удалён
        // гарантированным удалением, ТБ-064) чинится сбросом — иначе supersede вечно отказывал бы.
        var link = await db.DocumentIndexLinks
            .FirstOrDefaultAsync(l => l.DocumentId == documentId, cancellationToken);
        int? supersedes = null;
        if (link is not null)
        {
            await using var core = await coreFactory.CreateDbContextAsync(cancellationToken);
            var coreExists = await core.Documents.AnyAsync(d => d.Id == link.CoreDocumentId, cancellationToken);
            supersedes = coreExists ? link.CoreDocumentId : null;
        }

        var text = string.IsNullOrWhiteSpace(document.FullText)
            ? document.ShortContent
            : $"{document.ShortContent}\n\n{document.FullText}";

        var title = document.RegNumber is { } regNumber
            ? $"{regNumber} · {Truncate(document.ShortContent, 160)}"
            : Truncate(document.ShortContent, 180);

        var result = await ingestionPort.IngestAsync(
            new IngestionRequest(
                document.TypeName,
                title,
                text,
                document.Classification,
                document.DivisionId,
                Source: "Документооборот",
                DocDate: document.RegDate,
                Metadata: new Dictionary<string, string>
                {
                    // Обратная ссылка корпус → документооборот (для карточек-источников в ответах чата).
                    ["docflow_document_id"] = document.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["reg_number"] = document.RegNumber ?? string.Empty,
                    ["doc_group"] = document.Group.ToString(),
                },
                SupersedesDocumentId: supersedes),
            cancellationToken);

        if (!result.Accepted || result.DocumentId is not { } coreDocumentId)
        {
            return new DocumentIndexResult(DocumentIndexStatus.Rejected, Reason: result.RejectionReason);
        }

        // Мостик: одна актуальная строка на документ (вставка либо перенаправление на преемника).
        var now = DateTime.UtcNow;
        if (link is null)
        {
            db.DocumentIndexLinks.Add(new DocumentIndexLink
            {
                DocumentId = documentId,
                CoreDocumentId = coreDocumentId,
                IndexedAt = now,
            });
        }
        else
        {
            link.CoreDocumentId = coreDocumentId;
            link.IndexedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        // ChunkCount == 0 при Accepted — дедуп ядра: текст не менялся, событие изменения корпуса не наступило.
        if (result.ChunkCount == 0)
        {
            return new DocumentIndexResult(DocumentIndexStatus.Unchanged, coreDocumentId);
        }

        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Ingest,
                document.Classification,
                SubjectId: null,
                ObjectRef: $"docflow:document:{documentId}->core:{coreDocumentId};chunks:{result.ChunkCount}"),
            cancellationToken);

        return new DocumentIndexResult(DocumentIndexStatus.Indexed, coreDocumentId, result.ChunkCount);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

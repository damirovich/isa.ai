using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Documents;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Индексатор документов документооборота в корпус ядра (этап 7 Э4-35, ADR-0017 п.6) — первый
/// прикладной потребитель конвейера загрузки вне страницы «Загрузка корпуса».
/// </summary>
/// <remarks>
/// Механика: текст (краткое содержание + полный текст + текст АКТУАЛЬНЫХ файлов документа §3.3) →
/// <see cref="IIngestionPort"/> с грифом и подразделением ДОКУМЕНТА (fail-closed ТБ-024 соблюдён
/// по построению — поля NOT NULL); при переиндексации прежний корпусный документ гасится
/// supersede-механикой ядра (Э4-14); связь ведёт мостик <c>document_index_link</c> (одна строка на
/// документ). Идентичный текст дедуплицируется ядром по хешу (ТНД-002) — статус
/// <see cref="DocumentIndexStatus.Unchanged"/>. Успешная индексация аудируется
/// (<see cref="AuditAction.Ingest"/>, гриф записи — гриф документа).
///
/// Файлы: берутся только ВЕРСИОНИРУЕМЫЕ файлы документа с <c>IsLatest</c> (по одному на язык) —
/// прежние версии в корпус не попадают (их текст устарел так же, как сам файл), вложения не
/// индексируются намеренно (черновики и справочные материалы — не содержание документа).
/// Извлечение текста — ядровым <see cref="ITextExtractor"/>; нечитаемый или пустой файл (скан без
/// текстового слоя, ненастроенный OCR, битый файл) ПРОПУСКАЕТСЯ с записью в журнал — текст карточки
/// индексируется всегда, недоступность одного файла не должна выбрасывать документ из поиска.
/// </remarks>
public sealed class DocFlowDocumentIndexer(
    IDbContextFactory<DocFlowDbContext> docFlowFactory,
    IDbContextFactory<CoreDbContext> coreFactory,
    IIngestionPort ingestionPort,
    IAuditWriter auditWriter,
    ITextExtractor textExtractor,
    IDocFlowFileStorage fileStorage,
    ILogger<DocFlowDocumentIndexer> logger) : IDocumentIndexer
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

        // Содержимое актуальных файлов документа — тем же текстом, следом за карточкой: чанкер режет
        // по абзацам, и для поиска неважно, пришёл абзац из поля карточки или из файла.
        var fileTexts = await ExtractLatestFileTextsAsync(db, documentId, cancellationToken);
        if (fileTexts.Count > 0)
        {
            text = $"{text}\n\n{string.Join("\n\n", fileTexts)}";
        }

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

    /// <summary>
    /// Тексты АКТУАЛЬНЫХ файлов документа (по одному на язык, §3.3). Сбой одного файла — пропуск
    /// с записью в журнал, не отказ всей индексации: см. remarks класса.
    /// </summary>
    private async Task<List<string>> ExtractLatestFileTextsAsync(
        DocFlowDbContext db, int documentId, CancellationToken cancellationToken)
    {
        var latestFiles = await db.DocumentFiles.AsNoTracking()
            .Where(f => f.DocumentId == documentId && f.IsLatest)
            .OrderBy(f => f.Language).ThenBy(f => f.Id)
            .Select(f => new { f.FileName, f.StoredFileName })
            .ToListAsync(cancellationToken);

        var subPath = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var texts = new List<string>(latestFiles.Count);
        foreach (var file in latestFiles)
        {
            if (!textExtractor.CanExtract(file.FileName))
            {
                DocFlowDocumentIndexerLog.FileFormatUnsupported(logger, file.FileName, documentId);
                continue;
            }

            try
            {
                await using var content = await fileStorage.OpenReadAsync(
                    file.StoredFileName, FileCategories.Documents, subPath, cancellationToken);
                var extracted = await textExtractor.ExtractAsync(content, file.FileName, cancellationToken);
                if (string.IsNullOrWhiteSpace(extracted.Text))
                {
                    // Обычная причина — скан без текстового слоя: PDF-извлекатель его не читает (путь
                    // сканов — OCR), и молчаливо считать такой документ «проиндексированным целиком» нельзя.
                    DocFlowDocumentIndexerLog.FileTextEmpty(logger, file.FileName, documentId);
                    continue;
                }

                texts.Add(extracted.Text);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                DocFlowDocumentIndexerLog.FileExtractionFailed(logger, exception, file.FileName, documentId);
            }
        }

        return texts;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

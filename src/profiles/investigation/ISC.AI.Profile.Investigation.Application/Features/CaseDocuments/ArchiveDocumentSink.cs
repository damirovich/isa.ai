using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Investigation.Application.Features.CaseDocuments;

/// <summary>
/// Документы архива в «Следствии» (ADR-0042, ТФ-ДДЛ-01): документ документооборота группы «Хранение» с грифом и
/// подразделением дела, файл документа и привязка к делу — реализация порта пакета «Медиа» <see cref="IArchiveDocumentSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// Работает от имени ОПЕРАТОРА задания импорта под его допуском (<see cref="AccessContext"/> приходит от исполнителя,
/// прочитанный перед этим файлом): регистрация, файл и привязка проходят ту же решётку, что ручные сценарии
/// документооборота и дела (ТБ-020) — шире допуска оператора документ не ляжет. Сценарии Mediator здесь не годятся:
/// они берут субъекта из сессии, а задание идёт в фоне без неё.
/// </para>
/// <para>
/// Номер созданного документа сообщается вызывающему СРАЗУ (<c>onCreated</c>), до файла и привязки: прерванный приём
/// при повторе продолжает этот документ — догружает файл, если его нет, и привязывает к делу (привязка идемпотентна).
/// </para>
/// </remarks>
public sealed class ArchiveDocumentSink(
    IDocumentStore documents,
    IDocumentTypeStore types,
    ICaseDocumentStore links,
    IBackgroundTaskQueue queue) : IArchiveDocumentSink
{
    /// <summary>Вид фоновой задачи — тот же, что у регистрации документа в документообороте.</summary>
    public const string IndexingTaskKind = "Индексация документа в корпус ИИ";

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    /// <remarks>Только действующие типы группы «Хранение»: у архивного документа нет исполнителей и сроков.</remarks>
    public async Task<IReadOnlyList<ArchiveDocumentType>> ListTypesAsync(CancellationToken cancellationToken = default)
    {
        var storage = await types.ListAsync(DocumentGroup.Storage, isActive: true, cancellationToken);
        return storage.Select(t => new ArchiveDocumentType(t.Id, t.Name)).ToList();
    }

    /// <inheritdoc />
    public async Task<ArchiveDocumentResult> ImportAsync(
        ArchiveDocumentRequest request, AccessContext access, Func<int, Task> onCreated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(onCreated);

        var documentId = request.ExistingDocumentId;
        var hasFile = false;
        if (documentId is { } existing)
        {
            // Продолжение прерванной попытки: документ должен быть виден оператору; файл мог успеть лечь.
            var details = await documents.GetAsync(existing, access, cancellationToken);
            if (details is null)
            {
                return ArchiveDocumentResult.Fail($"документ № {existing} прежней попытки недоступен — строку нужно ввести заново");
            }

            hasFile = details.Files.Count > 0;
        }
        else
        {
            var created = await documents.CreateAsync(
                new DocumentDraft(
                    request.RegNumber,
                    request.RegDate,
                    request.TypeId,
                    DocumentDirection.Internal,
                    request.Source,
                    request.Title,
                    FullText: null,
                    request.Notes,
                    Priority: null,
                    InspectorUserId: null,
                    request.Case.Classification,
                    request.Case.DivisionId,
                    access.NumericSubjectId),
                [], useCommonDeadline: false, commonDeadline: null, access, cancellationToken);
            if (created.Status != DocumentWriteStatus.Ok)
            {
                return ArchiveDocumentResult.Fail(Explain(created.Status));
            }

            documentId = created.DocumentId;
            await onCreated(created.DocumentId);
        }

        if (!hasFile)
        {
            var uploaded = await documents.AddDocumentFileAsync(
                documentId.Value,
                new UploadedFile(request.FileName, request.ContentType, request.Content),
                DocumentLanguage.Unspecified,
                access,
                cancellationToken);
            if (uploaded != DocumentWriteStatus.Ok)
            {
                return ArchiveDocumentResult.Fail($"документ № {documentId} зарегистрирован, но файл не прикреплён");
            }
        }

        var linked = await links.AttachAsync(request.Case.CaseId, documentId.Value, access, cancellationToken);
        return linked is CaseDocumentWriteResult.Ok or CaseDocumentWriteResult.AlreadyLinked
            ? new ArchiveDocumentResult(documentId, null)
            : ArchiveDocumentResult.Fail($"документ № {documentId} зарегистрирован, но не прикреплён к делу");
    }

    /// <inheritdoc />
    public Task<Guid> EnqueueIndexingAsync(int documentId, CancellationToken cancellationToken = default) =>
        queue.EnqueueAsync(
            IndexingTaskKind,
            async (sp, ct) => await sp.GetRequiredService<IDocumentIndexer>().IndexAsync(documentId, ct),
            cancellationToken).AsTask();

    private static string Explain(DocumentWriteStatus status) => status switch
    {
        DocumentWriteStatus.TypeUnavailable => "тип документа не найден или выключен",
        DocumentWriteStatus.RegNumberTaken => "документ с таким рег. номером уже зарегистрирован",
        DocumentWriteStatus.ExecutionFieldsMissing => "тип документа группы «Исполнение» — для архива нужен тип группы «Хранение»",
        DocumentWriteStatus.ClassificationOutsideClearance => "гриф дела выше допуска оператора",
        DocumentWriteStatus.DivisionOutsideClearance => "подразделение дела вне допуска оператора",
        _ => "документ не зарегистрирован",
    };
}

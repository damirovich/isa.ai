using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>Поставка по умолчанию: экземпляр без документооборота документы архива не принимает.</summary>
public sealed class UnsupportedArchiveDocumentSink : IArchiveDocumentSink
{
    /// <summary>Отказ строки-документа.</summary>
    public const string NotSupportedMessage = "документы этот экземпляр не принимает: нет документооборота";

    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<ArchiveDocumentType>> ListTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ArchiveDocumentType>>([]);

    /// <inheritdoc />
    public Task<ArchiveDocumentResult> ImportAsync(
        ArchiveDocumentRequest request, AccessContext access, Func<int, Task> onCreated, CancellationToken cancellationToken = default) =>
        Task.FromResult(ArchiveDocumentResult.Fail(NotSupportedMessage));

    /// <inheritdoc />
    public Task<Guid> EnqueueIndexingAsync(int documentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotSupportedMessage);
}

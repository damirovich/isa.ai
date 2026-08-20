using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>
/// Привязать документ корпуса к норме, а его чанки — к редакции. Именно эта связка делает смену
/// статуса редакции действенной: материализатор гасит чанки по <c>ChunkRevisionLink</c> (GATE-3).
/// </summary>
public sealed record LinkNormDocumentCommand(int NormId, int RevisionId, int CoreDocumentId)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:norm:{NormId}:link:revision={RevisionId};document={CoreDocumentId}";

    /// <inheritdoc cref="LinkNormDocumentCommand" />
    public sealed class Handler(INormRegistryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<LinkNormDocumentCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            LinkNormDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await NormGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(NormGuard.Denied);
            }

            var (result, linkedChunks) = await store.LinkDocumentAsync(
                command.NormId, command.RevisionId, command.CoreDocumentId, cancellationToken);
            return result switch
            {
                NormWriteResult.Ok => ResponseDto<int>.Ok(
                    linkedChunks, $"Документ привязан; фрагментов к редакции: {linkedChunks}."),
                NormWriteResult.AlreadyLinked => ResponseDto<int>.Conflict("Документ уже привязан к этой норме."),
                _ => ResponseDto<int>.NotFound("Норма, редакция или документ корпуса не найдены."),
            };
        }
    }
}

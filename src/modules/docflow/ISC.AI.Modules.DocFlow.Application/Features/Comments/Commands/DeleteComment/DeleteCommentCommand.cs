using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Comments;

/// <summary>Удалить свой комментарий (§4.8): мягко, содержимое перестаёт отдаваться.</summary>
public sealed record DeleteCommentCommand(int DocumentId, int CommentId)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:comment:{CommentId}:delete";

    /// <inheritdoc cref="DeleteCommentCommand" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider)
        : IRequestHandler<DeleteCommentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            DeleteCommentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } actorId)
            {
                return ResponseDto<bool>.BadRequest("Удаление комментария требует аутентифицированного пользователя.");
            }

            if (await documents.GetAsync(command.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<bool>.NotFound("Документ не найден.");
            }

            var status = await comments.DeleteAsync(command.CommentId, actorId, cancellationToken);
            return CommentResponses.ToResponse(status);
        }
    }
}

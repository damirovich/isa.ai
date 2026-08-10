using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Comments;

/// <summary>Закрыть/переоткрыть обсуждение (§4.8) — только корневой комментарий.</summary>
public sealed record SetCommentResolvedCommand(int DocumentId, int CommentId, bool Resolved)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:comment:{CommentId}:{(Resolved ? "resolve" : "unresolve")}";

    /// <inheritdoc cref="SetCommentResolvedCommand" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider)
        : IRequestHandler<SetCommentResolvedCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetCommentResolvedCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } actorId)
            {
                return ResponseDto<bool>.BadRequest("Действие требует аутентифицированного пользователя.");
            }

            if (await documents.GetAsync(command.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<bool>.NotFound("Документ не найден.");
            }

            var status = await comments.SetResolvedAsync(
                command.CommentId, command.Resolved, actorId, cancellationToken);
            return CommentResponses.ToResponse(status);
        }
    }
}

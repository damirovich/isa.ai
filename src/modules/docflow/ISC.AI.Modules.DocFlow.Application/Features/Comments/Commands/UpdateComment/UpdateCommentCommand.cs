using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Comments;

/// <summary>Изменить свой комментарий (§4.8): правится только текст и упоминания.</summary>
public sealed record UpdateCommentCommand(
    int DocumentId, int CommentId, string Content, IReadOnlyList<int> MentionedUserIds)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:comment:{CommentId}:edit";

    /// <inheritdoc cref="UpdateCommentCommand" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider)
        : IRequestHandler<UpdateCommentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UpdateCommentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } editorId)
            {
                return ResponseDto<bool>.BadRequest("Правка комментария требует аутентифицированного пользователя.");
            }

            if (await documents.GetAsync(command.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<bool>.NotFound("Документ не найден.");
            }

            var status = await comments.UpdateAsync(
                command.CommentId, command.Content, command.MentionedUserIds, editorId, cancellationToken);
            return CommentResponses.ToResponse(status);
        }
    }
}

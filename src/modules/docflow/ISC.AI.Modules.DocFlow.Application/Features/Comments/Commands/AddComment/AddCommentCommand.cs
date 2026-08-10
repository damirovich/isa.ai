using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Comments;

/// <summary>Добавить комментарий или ответ (§4.8).</summary>
public sealed record AddCommentCommand(
    int DocumentId,
    int? ParentCommentId,
    string Content,
    CommentType CommentType,
    IReadOnlyList<int> MentionedUserIds,
    IReadOnlyList<UploadedFile>? Files = null) : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Текст комментария в журнал НЕ пишется — только факт и объект (ТБ-032).</remarks>
    public string? AuditSummary => $"docflow:document:{DocumentId}:comment:add";

    /// <inheritdoc cref="AddCommentCommand" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider,
        DocFlowEventNotifier notifier)
        : IRequestHandler<AddCommentCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(AddCommentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } authorId)
            {
                return ResponseDto<int>.BadRequest("Комментарий требует аутентифицированного пользователя.");
            }

            var document = await documents.GetAsync(command.DocumentId, access, cancellationToken);
            if (document is null)
            {
                return ResponseDto<int>.NotFound("Документ не найден.");
            }

            var (status, commentId) = await comments.AddAsync(
                new CommentDraft(
                    command.DocumentId, command.ParentCommentId, command.Content, command.CommentType,
                    command.MentionedUserIds, command.Files),
                authorId, cancellationToken);

            if (status == CommentWriteStatus.Ok)
            {
                // Разд. 5: упомянутым — «вас упомянули», остальным участникам — «добавлен комментарий».
                // Сбой уведомления не отменяет уже сохранённый комментарий (см. DocFlowEventNotifier).
                await DocFlowEventNotifier.SafeAsync(() => notifier.CommentAddedAsync(
                    document, commentId, authorId, command.MentionedUserIds, cancellationToken));
            }

            return status switch
            {
                CommentWriteStatus.Ok => ResponseDto<int>.Ok(commentId),
                CommentWriteStatus.NotFound => ResponseDto<int>.NotFound("Документ или комментарий не найден."),
                CommentWriteStatus.ParentMismatch =>
                    ResponseDto<int>.BadRequest("Ответ относится к комментарию другого документа."),
                _ => ResponseDto<int>.Fail("Не удалось добавить комментарий."),
            };
        }
    }
}

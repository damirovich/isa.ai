using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Documents;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Comments;

/// <inheritdoc cref="AddCommentValidator" />
public sealed class UpdateCommentValidator : AbstractValidator<UpdateCommentCommand>
{
    /// <summary>Правила правки: только текст (файлы и вид комментария неизменяемы — как в СКИД).</summary>
    public UpdateCommentValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.CommentId).GreaterThan(0);
        RuleFor(c => c.Content).NotEmpty().WithMessage("Комментарий не может быть пустым.")
            .MaximumLength(AddCommentValidator.MaxContentLength);
    }
}

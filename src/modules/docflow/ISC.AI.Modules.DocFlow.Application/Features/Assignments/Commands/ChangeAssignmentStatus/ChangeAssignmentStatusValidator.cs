using FluentValidation;
using ISC.AI.Modules.DocFlow.Application.Features.Documents;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <inheritdoc cref="RegisterDocumentValidator" />
public sealed class ChangeAssignmentStatusValidator : AbstractValidator<ChangeAssignmentStatusCommand>
{
    /// <summary>Правила формы §4.2 (+ файловые лимиты перехода).</summary>
    public ChangeAssignmentStatusValidator()
    {
        RuleFor(c => c.AssignmentId).GreaterThan(0);
        RuleFor(c => c.NewStatus).IsInEnum();
        RuleFor(c => c.Comment).MaximumLength(2000);
        this.ApplyFileListRules(c => c.Files);
    }
}

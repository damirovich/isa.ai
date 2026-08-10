using FluentValidation;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <summary>Правила переназначения исполнителя (§4.7): основание НЕобязательно — как в СКИД.</summary>
public sealed class ReassignAssigneeValidator : AbstractValidator<ReassignAssigneeCommand>
{
    /// <summary>Максимальная длина основания — как у основания продления срока.</summary>
    public const int MaxReasonLength = 2000;

    /// <inheritdoc cref="ReassignAssigneeValidator" />
    public ReassignAssigneeValidator()
    {
        RuleFor(c => c.AssignmentId).GreaterThan(0);
        RuleFor(c => c.NewAssigneeUserId).GreaterThan(0)
            .WithMessage("Укажите нового исполнителя.");
        RuleFor(c => c.Reason).MaximumLength(MaxReasonLength);
    }
}

using FluentValidation;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Правила правки учётной записи.</summary>
public sealed class UpdateUserAccountValidator : AbstractValidator<UpdateUserAccountCommand>
{
    /// <inheritdoc cref="UpdateUserAccountValidator" />
    public UpdateUserAccountValidator()
    {
        RuleFor(c => c.UserId).GreaterThan(0);
        RuleFor(c => c.DisplayName).MaximumLength(200);
        RuleFor(c => c.Position).MaximumLength(200);
    }
}

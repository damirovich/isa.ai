using FluentValidation;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Правила создания учётной записи.</summary>
public sealed class CreateUserAccountValidator : AbstractValidator<CreateUserAccountCommand>
{
    /// <inheritdoc cref="CreateUserAccountValidator" />
    public CreateUserAccountValidator()
    {
        RuleFor(c => c.UserName).NotEmpty().WithMessage("Укажите имя входа.")
            .MaximumLength(100)
            .Matches("^[a-zA-Z0-9._-]+$")
                .WithMessage("Имя входа: латиница, цифры, точка, дефис, подчёркивание.");
        RuleFor(c => c.DisplayName).MaximumLength(200);
    }
}

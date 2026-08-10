using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Accounts;

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

using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Accounts;

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

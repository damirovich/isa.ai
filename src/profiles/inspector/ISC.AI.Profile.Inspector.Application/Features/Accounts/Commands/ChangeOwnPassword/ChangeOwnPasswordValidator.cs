using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Accounts;

/// <summary>Правила смены собственного пароля.</summary>
public sealed class ChangeOwnPasswordValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    /// <summary>
    /// Минимальная длина нового пароля. Требований к составу (цифра/регистр/спецсимвол) НЕТ
    /// намеренно: они гонят людей к «Пароль1!» и записям на бумаге, тогда как длина даёт стойкость
    /// честно. Значение согласуется с временным паролем (12 символов из криптогенератора).
    /// </summary>
    public const int MinPasswordLength = 10;

    /// <inheritdoc cref="ChangeOwnPasswordValidator" />
    public ChangeOwnPasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).NotEmpty().WithMessage("Укажите текущий пароль.");
        RuleFor(c => c.NewPassword).NotEmpty()
            .MinimumLength(MinPasswordLength)
                .WithMessage($"Новый пароль — не короче {MinPasswordLength} символов.")
            .MaximumLength(200);
    }
}

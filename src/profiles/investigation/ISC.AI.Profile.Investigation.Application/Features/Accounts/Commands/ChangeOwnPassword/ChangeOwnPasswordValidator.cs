using FluentValidation;

namespace ISC.AI.Profile.Investigation.Application.Features.Accounts;

/// <summary>Правила смены собственного пароля.</summary>
public sealed class ChangeOwnPasswordValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    /// <summary>
    /// Минимальная длина нового пароля. Требований к составу НЕТ намеренно: они гонят людей к «Пароль1!»
    /// и записям на бумаге, тогда как длина даёт стойкость честно.
    /// </summary>
    public const int MinPasswordLength = 10;

    /// <inheritdoc cref="ChangeOwnPasswordValidator" />
    public ChangeOwnPasswordValidator()
    {
        RuleFor(c => c.CurrentPassword).NotEmpty().WithMessage("Укажите текущий пароль.");
        RuleFor(c => c.NewPassword).NotEmpty()
            .MinimumLength(MinPasswordLength).WithMessage($"Новый пароль — не короче {MinPasswordLength} символов.")
            .MaximumLength(200);
    }
}

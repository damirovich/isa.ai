using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>Пределы полей нормы — те же, что в схеме (identifier ≤ 200, title ≤ 1000).</summary>
public sealed class CreateNormValidator : AbstractValidator<CreateNormCommand>
{
    /// <inheritdoc cref="CreateNormValidator" />
    public CreateNormValidator()
    {
        RuleFor(c => c.Identifier)
            .NotEmpty().WithMessage("Номер НПА обязателен.")
            .MaximumLength(200).WithMessage("Номер НПА — не длиннее 200 символов.");
        RuleFor(c => c.Title)
            .NotEmpty().WithMessage("Название НПА обязательно.")
            .MaximumLength(1000).WithMessage("Название НПА — не длиннее 1000 символов.");
    }
}

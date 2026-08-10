using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>Те же пределы, что при создании (identifier ≤ 200, title ≤ 1000).</summary>
public sealed class UpdateNormValidator : AbstractValidator<UpdateNormCommand>
{
    /// <inheritdoc cref="UpdateNormValidator" />
    public UpdateNormValidator()
    {
        RuleFor(c => c.Identifier)
            .NotEmpty().WithMessage("Номер НПА обязателен.")
            .MaximumLength(200).WithMessage("Номер НПА — не длиннее 200 символов.");
        RuleFor(c => c.Title)
            .NotEmpty().WithMessage("Название НПА обязательно.")
            .MaximumLength(1000).WithMessage("Название НПА — не длиннее 1000 символов.");
    }
}

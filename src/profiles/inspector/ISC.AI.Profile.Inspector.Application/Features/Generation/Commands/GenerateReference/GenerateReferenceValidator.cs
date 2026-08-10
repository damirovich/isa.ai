using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Generation;

/// <summary>Валидатор команды генерации справки: тема обязательна и осмысленной длины.</summary>
public sealed class GenerateReferenceValidator : AbstractValidator<GenerateReferenceCommand>
{
    /// <summary>Создаёт правила валидации команды.</summary>
    public GenerateReferenceValidator()
    {
        RuleFor(c => c.Topic)
            .NotEmpty().WithMessage("Тема справки обязательна.")
            .MinimumLength(3).WithMessage("Тема слишком короткая.")
            .MaximumLength(500).WithMessage("Тема слишком длинная.");
    }
}

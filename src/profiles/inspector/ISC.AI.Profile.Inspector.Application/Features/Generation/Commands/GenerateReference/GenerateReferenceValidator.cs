using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Generation;

/// <summary>
/// Валидатор команды Генератора: тема обязательна и осмысленной длины; тип документа —
/// только из перечня <see cref="ReferenceDocumentTypes"/> (у каждого типа свой промпт-шаблон).
/// </summary>
public sealed class GenerateReferenceValidator : AbstractValidator<GenerateReferenceCommand>
{
    /// <summary>Создаёт правила валидации команды.</summary>
    public GenerateReferenceValidator()
    {
        RuleFor(c => c.Topic)
            .NotEmpty().WithMessage("Тема документа обязательна.")
            .MinimumLength(3).WithMessage("Тема слишком короткая.")
            .MaximumLength(500).WithMessage("Тема слишком длинная.");

        RuleFor(c => c.DocumentType)
            .Must(type => ReferenceDocumentTypes.TemplateKeys.ContainsKey(type))
            .WithMessage("Неизвестный тип документа.");
    }
}

using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Generation;

/// <summary>Валидатор команды экспорта: заголовок и тело обязательны.</summary>
public sealed class ExportReferenceValidator : AbstractValidator<ExportReferenceCommand>
{
    /// <summary>Создаёт правила валидации команды экспорта.</summary>
    public ExportReferenceValidator()
    {
        RuleFor(c => c.Title).NotEmpty().WithMessage("Заголовок обязателен.");
        RuleFor(c => c.Body).NotEmpty().WithMessage("Текст документа обязателен.");
    }
}

using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Loading;

/// <summary>Валидатор загрузки файла: имя, тип и содержимое обязательны.</summary>
public sealed class IngestFileValidator : AbstractValidator<IngestFileCommand>
{
    /// <summary>Создаёт правила валидации команды загрузки файла.</summary>
    public IngestFileValidator()
    {
        RuleFor(c => c.FileName).NotEmpty().WithMessage("Имя файла обязательно.");
        RuleFor(c => c.DocType).NotEmpty().WithMessage("Тип документа обязателен.");
        RuleFor(c => c.Content).NotEmpty().WithMessage("Файл пуст.");
    }
}

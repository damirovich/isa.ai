using FluentValidation;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Inspector.Application.Features.Loading;

/// <summary>Валидатор загрузки файла: имя, тип и содержимое обязательны, гриф — в пределах шкалы платформы.</summary>
public sealed class IngestFileValidator : AbstractValidator<IngestFileCommand>
{
    /// <summary>Создаёт правила валидации команды загрузки файла.</summary>
    public IngestFileValidator()
    {
        RuleFor(c => c.FileName).NotEmpty().WithMessage("Имя файла обязательно.");
        RuleFor(c => c.DocType).NotEmpty().WithMessage("Тип документа обязателен.");
        RuleFor(c => c.Content).NotEmpty().WithMessage("Файл пуст.");
        RuleFor(c => c.Classification)
            .InclusiveBetween(ClassificationLevels.Unclassified, ClassificationLevels.Max)
            .WithMessage("Гриф — от «Без грифа» до «Особой важности» (ADR-0030).");
    }
}

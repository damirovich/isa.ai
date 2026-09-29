using FluentValidation;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Inspector.Application.Features.Loading;

/// <summary>Валидатор импорта пакета: путь к манифесту и подразделение обязательны, гриф — в пределах шкалы платформы.</summary>
public sealed class ImportBundleValidator : AbstractValidator<ImportBundleCommand>
{
    /// <summary>Создаёт правила валидации команды импорта пакета.</summary>
    public ImportBundleValidator()
    {
        RuleFor(c => c.ManifestPath).NotEmpty().WithMessage("Укажите путь к манифесту пакета.");
        RuleFor(c => c.DivisionId).GreaterThan(0).WithMessage("Укажите подразделение пакета.");
        RuleFor(c => c.Classification)
            .InclusiveBetween(ClassificationLevels.Unclassified, ClassificationLevels.Max)
            .WithMessage("Гриф пакета — от «Без грифа» до «Особой важности» (ADR-0030).");
    }
}

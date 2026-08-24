using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>Пределы входа генерации методики — форма короткая, простыни уходят в «доп. указания».</summary>
public sealed class GenerateMethodDocumentValidator : AbstractValidator<GenerateMethodDocumentCommand>
{
    /// <inheritdoc cref="GenerateMethodDocumentValidator" />
    public GenerateMethodDocumentValidator()
    {
        RuleFor(c => c.ArtifactKind).NotEmpty().WithMessage("Выберите вид документа.").MaximumLength(100);
        RuleFor(c => c.InspectionType).NotEmpty().WithMessage("Укажите тип проверки.").MaximumLength(200);
        RuleFor(c => c.Scope).NotEmpty().WithMessage("Укажите объект проверки.").MaximumLength(300);
        RuleFor(c => c.Extra).MaximumLength(2000);
    }
}

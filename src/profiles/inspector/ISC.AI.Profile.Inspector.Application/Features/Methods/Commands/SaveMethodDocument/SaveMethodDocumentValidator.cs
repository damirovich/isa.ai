using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>Пределы сохранения методики — те же, что в схеме (100/200/300; текст без предела схемы).</summary>
public sealed class SaveMethodDocumentValidator : AbstractValidator<SaveMethodDocumentCommand>
{
    /// <inheritdoc cref="SaveMethodDocumentValidator" />
    public SaveMethodDocumentValidator()
    {
        RuleFor(c => c.ArtifactKind).NotEmpty().MaximumLength(100);
        RuleFor(c => c.InspectionType).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Scope).NotEmpty().MaximumLength(300);
        RuleFor(c => c.Body).NotEmpty().WithMessage("Пустой текст сохранять не во что.");
        RuleFor(c => c.Classification).GreaterThanOrEqualTo((short)0);
    }
}

using FluentValidation;

namespace ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;

/// <inheritdoc cref="CreateDocumentTypeValidator" />
public sealed class UpdateDocumentTypeValidator : AbstractValidator<UpdateDocumentTypeCommand>
{
    /// <summary>Правила: идентификатор положительный; имя обязательно и ≤200.</summary>
    public UpdateDocumentTypeValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование типа документа.")
            .MaximumLength(200).WithMessage("Наименование не длиннее 200 символов.");
    }
}

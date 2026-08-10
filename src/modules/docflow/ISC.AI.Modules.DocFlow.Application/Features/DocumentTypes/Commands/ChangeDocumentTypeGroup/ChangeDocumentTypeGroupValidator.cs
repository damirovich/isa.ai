using FluentValidation;

namespace ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;

/// <inheritdoc cref="CreateDocumentTypeValidator" />
public sealed class ChangeDocumentTypeGroupValidator : AbstractValidator<ChangeDocumentTypeGroupCommand>
{
    /// <summary>Правила: идентификатор положительный; группа — из известного набора.</summary>
    public ChangeDocumentTypeGroupValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);
        RuleFor(c => c.NewGroup).IsInEnum().WithMessage("Укажите группу: «Хранение» или «Исполнение».");
    }
}

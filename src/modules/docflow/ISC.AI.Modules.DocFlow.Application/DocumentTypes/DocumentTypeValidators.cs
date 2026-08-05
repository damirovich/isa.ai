using FluentValidation;

namespace ISC.AI.Modules.DocFlow.Application.DocumentTypes;

/// <summary>
/// Валидаторы формы справочника типов (сквозной <c>ValidationBehavior</c> хоста; ТЗ СКИД §3.1).
/// Только правила формы — уникальность имени проверяет хранилище (плюс unique-индекс БД),
/// чтобы валидаторы оставались синхронными и юнит-тестируемыми без БД.
/// </summary>
public sealed class CreateDocumentTypeValidator : AbstractValidator<CreateDocumentTypeCommand>
{
    /// <summary>Правила: имя обязательно и ≤200; группа — из известного набора.</summary>
    public CreateDocumentTypeValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование типа документа.")
            .MaximumLength(200).WithMessage("Наименование не длиннее 200 символов.");
        RuleFor(c => c.Group).IsInEnum().WithMessage("Укажите группу: «Хранение» или «Исполнение».");
    }
}

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

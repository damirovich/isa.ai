using FluentValidation;

namespace ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;

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

using FluentValidation;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

/// <inheritdoc cref="RegisterDocumentValidator" />
/// <summary>
/// Правила добавления назначения (§4.1). Обязательны только документ и подразделение: назначение
/// «на подразделение», без исполнителя и без срока, — законное состояние (перенос решения СКИД,
/// делать форму строже оригинала незачем). Остальное — дубль подразделения, группа документа,
/// допуск исполнителя — проверяется в хранилище: этим правилам нужна БД.
/// </summary>
public sealed class AddAssignmentValidator : AbstractValidator<AddAssignmentCommand>
{
    /// <inheritdoc cref="AddAssignmentValidator" />
    public AddAssignmentValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.DivisionId).GreaterThan(0)
            .WithMessage("Для назначения необходимо указать подразделение.");
        RuleFor(c => c.AssigneeUserId).GreaterThan(0)
            .When(c => c.AssigneeUserId is not null)
            .WithMessage("Некорректный исполнитель.");
    }
}

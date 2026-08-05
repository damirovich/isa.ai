using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Divisions;

/// <summary>
/// Сценарии ведения справочника подразделений (§4.2): иерархия ТУ→РО, код — ключ сопоставления со СКИД.
/// Справочник питает и решётку доступа (допуски по подразделениям), и модуль документооборота
/// (<c>IDivisionDirectory</c>, вопрос 3 Э4-35).
/// </summary>

/// <summary>Все подразделения плоским списком (дерево строит страница по <c>ParentId</c>).</summary>
public sealed record ListDivisionTreeQuery : IRequest<ResponseDto<IReadOnlyList<DivisionNode>>>
{
    /// <inheritdoc cref="ListDivisionTreeQuery" />
    public sealed class Handler(IDivisionAdminStore store)
        : IRequestHandler<ListDivisionTreeQuery, ResponseDto<IReadOnlyList<DivisionNode>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<DivisionNode>>> Handle(
            ListDivisionTreeQuery query, CancellationToken cancellationToken)
        {
            var items = await store.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<DivisionNode>>.Ok(items, items.Count);
        }
    }
}

/// <summary>Создать подразделение (корневое либо дочернее к <paramref name="ParentId"/>).</summary>
public sealed record CreateDivisionCommand(string Name, string? Code, int? ParentId)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:division:create:{Name}";

    /// <inheritdoc cref="CreateDivisionCommand" />
    public sealed class Handler(IDivisionAdminStore store) : IRequestHandler<CreateDivisionCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            CreateDivisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var id = await store.CreateAsync(command.Name, command.Code, command.ParentId, cancellationToken);
            return ResponseDto<int>.Ok(id);
        }
    }
}

/// <summary>Переименовать подразделение / изменить код сопоставления со СКИД.</summary>
public sealed record RenameDivisionCommand(int Id, string Name, string? Code)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:division:rename:{Id}";

    /// <inheritdoc cref="RenameDivisionCommand" />
    public sealed class Handler(IDivisionAdminStore store) : IRequestHandler<RenameDivisionCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            RenameDivisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var found = await store.RenameAsync(command.Id, command.Name, command.Code, cancellationToken);
            return found ? ResponseDto<bool>.Ok(true) : ResponseDto<bool>.NotFound("Подразделение не найдено.");
        }
    }
}

/// <summary>Валидатор создания подразделения (форма §4.2).</summary>
public sealed class CreateDivisionValidator : AbstractValidator<CreateDivisionCommand>
{
    /// <summary>Правила: имя обязательно ≤500; код ≤100.</summary>
    public CreateDivisionValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование подразделения.").MaximumLength(500);
        RuleFor(c => c.Code).MaximumLength(100);
    }
}

/// <inheritdoc cref="CreateDivisionValidator" />
public sealed class RenameDivisionValidator : AbstractValidator<RenameDivisionCommand>
{
    /// <summary>Правила: идентификатор положительный; имя обязательно ≤500; код ≤100.</summary>
    public RenameDivisionValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование подразделения.").MaximumLength(500);
        RuleFor(c => c.Code).MaximumLength(100);
    }
}

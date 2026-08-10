using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Divisions;

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

using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.ViolationCategories;

/// <summary>
/// Классификатор видов нарушений плоским списком (дерево строит UI). При ПУСТОМ классификаторе
/// сначала заводится стартовый набор сфер прототипа (идемпотентно, см. порт): иначе экран нарушений
/// встречал бы оператора пустым выбором, а «сначала сходите в другой экран» — плохой первый опыт.
/// </summary>
public sealed record ListViolationCategoriesQuery
    : IRequest<ResponseDto<IReadOnlyList<ViolationCategoryNode>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "inspector:violation-categories:list";

    /// <inheritdoc cref="ListViolationCategoriesQuery" />
    public sealed class Handler(IViolationCategoryStore store)
        : IRequestHandler<ListViolationCategoriesQuery, ResponseDto<IReadOnlyList<ViolationCategoryNode>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ViolationCategoryNode>>> Handle(
            ListViolationCategoriesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            await store.SeedDefaultsAsync(cancellationToken);
            var nodes = await store.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<ViolationCategoryNode>>.Ok(nodes);
        }
    }
}

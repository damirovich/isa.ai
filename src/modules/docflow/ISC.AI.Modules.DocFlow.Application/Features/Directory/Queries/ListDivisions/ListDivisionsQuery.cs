using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Directory;

/// <summary>Справочник подразделений для выбора (владелец документа, назначения §4.1).</summary>
public sealed record ListDivisionsQuery : IRequest<ResponseDto<IReadOnlyList<DivisionItem>>>
{
    /// <inheritdoc cref="ListDivisionsQuery" />
    public sealed class Handler(IDivisionDirectory directory)
        : IRequestHandler<ListDivisionsQuery, ResponseDto<IReadOnlyList<DivisionItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<DivisionItem>>> Handle(
            ListDivisionsQuery query, CancellationToken cancellationToken)
        {
            var items = await directory.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<DivisionItem>>.Ok(items, items.Count);
        }
    }
}

using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Clearances;

/// <summary>Справочник подразделений для выбора в допуске (номер + наименование).</summary>
public sealed record ListClearanceDivisionsQuery : IRequest<ResponseDto<IReadOnlyList<ClearanceDivision>>>
{
    /// <inheritdoc cref="ListClearanceDivisionsQuery" />
    public sealed class Handler(IDivisionCatalog divisions, IPlatformAdministration administration)
        : IRequestHandler<ListClearanceDivisionsQuery, ResponseDto<IReadOnlyList<ClearanceDivision>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ClearanceDivision>>> Handle(
            ListClearanceDivisionsQuery query, CancellationToken cancellationToken)
        {
            // ИНВАРИАНТ (ТБ-012): состав подразделений в форме допуска — то же режимное сведение.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<IReadOnlyList<ClearanceDivision>>.BadRequest(AdminGuard.Denied);
            }

            // Недействующие подразделения НЕ отфильтрованы намеренно: допуск живёт дольше
            // подразделения, и администратору нужно уметь как объяснить уже выданный номер, так и
            // повторить его при замене допуска.
            var items = (await divisions.ListAsync(cancellationToken))
                .Select(d => new ClearanceDivision(d.Id, d.Name))
                .ToList();

            return ResponseDto<IReadOnlyList<ClearanceDivision>>.Ok(items, items.Count);
        }
    }
}

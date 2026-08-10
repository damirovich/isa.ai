using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Clearances;

/// <summary>Справочник подразделений для выбора в допуске (номер + наименование).</summary>
public sealed record ListClearanceDivisionsQuery : IRequest<ResponseDto<IReadOnlyList<ClearanceDivision>>>
{
    /// <inheritdoc cref="ListClearanceDivisionsQuery" />
    public sealed class Handler(
        IDivisionAdminStore divisions, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ListClearanceDivisionsQuery, ResponseDto<IReadOnlyList<ClearanceDivision>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ClearanceDivision>>> Handle(
            ListClearanceDivisionsQuery query, CancellationToken cancellationToken)
        {
            if (!await ClearanceGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<ClearanceDivision>>.BadRequest(ClearanceGuard.Denied);
            }

            var items = (await divisions.ListAsync(cancellationToken))
                .Select(d => new ClearanceDivision(d.Id, d.Name))
                .ToList();

            return ResponseDto<IReadOnlyList<ClearanceDivision>>.Ok(items, items.Count);
        }
    }
}

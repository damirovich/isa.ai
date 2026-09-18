using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Scope;

/// <summary>
/// Фигуранты дела для привязки кандидата на стадии ЭКСПЕРТА (ТФ-ВЕР-03). Верификатору список не
/// предлагается вовсе — слепота второй стадии (ТФ-ВЕР-02) обеспечивается на уровне страницы и команды.
/// Дело вне допуска/роли неотличимо от несуществующего (ТБ-020/021).
/// </summary>
public sealed record ListCasePersonsQuery(int CaseId) : IRequest<ResponseDto<IReadOnlyList<CasePersonItem>>>
{
    /// <inheritdoc cref="ListCasePersonsQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, ICaseScope caseScope)
        : IRequestHandler<ListCasePersonsQuery, ResponseDto<IReadOnlyList<CasePersonItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CasePersonItem>>> Handle(
            ListCasePersonsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021): сначала доступность дела, затем — его фигуранты.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var caseItem = await caseScope.GetCaseAsync(query.CaseId, access, cancellationToken);
            if (caseItem is null)
            {
                return ResponseDto<IReadOnlyList<CasePersonItem>>.NotFound("Дело не найдено или недоступно.");
            }

            var persons = await caseScope.ListPersonsAsync(caseItem.CaseId, access, cancellationToken);
            return ResponseDto<IReadOnlyList<CasePersonItem>>.Ok(persons, persons.Count);
        }
    }
}

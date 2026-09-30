using System;
using System.Collections.Generic;
using System.Linq;
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
/// <param name="CaseId">Дело кандидата.</param>
/// <param name="FaceId">
/// Лицо кандидата: если задано, у фигурантов отмечается <see cref="CasePersonItem.ConfirmedOnFace"/> — это лицо у
/// них уже подтверждено (повторное подтверждение нового появления не создаст).
/// </param>
public sealed record ListCasePersonsQuery(int CaseId, int? FaceId = null) : IRequest<ResponseDto<IReadOnlyList<CasePersonItem>>>
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
            if (query.FaceId is { } faceId)
            {
                var confirmed = await caseScope.ListPersonsConfirmedOnFaceAsync(caseItem.CaseId, faceId, access, cancellationToken);
                persons = persons.Select(p => p with { ConfirmedOnFace = confirmed.Contains(p.PersonId) }).ToList();
            }

            return ResponseDto<IReadOnlyList<CasePersonItem>>.Ok(persons, persons.Count);
        }
    }
}

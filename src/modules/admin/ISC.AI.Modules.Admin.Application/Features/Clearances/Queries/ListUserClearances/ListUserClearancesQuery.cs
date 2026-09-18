using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Clearances;

/// <summary>Все активные пользователи с их допусками (экран администрирования).</summary>
public sealed record ListUserClearancesQuery : IRequest<ResponseDto<IReadOnlyList<UserClearanceRow>>>
{
    /// <inheritdoc cref="ListUserClearancesQuery" />
    public sealed class Handler(
        IClearanceStore clearances,
        IDivisionCatalog divisions,
        IPlatformAdministration administration)
        : IRequestHandler<ListUserClearancesQuery, ResponseDto<IReadOnlyList<UserClearanceRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserClearanceRow>>> Handle(
            ListUserClearancesQuery query, CancellationToken cancellationToken)
        {
            // ИНВАРИАНТ (ТБ-012): кто вправе видеть и выдавать допуски — решает ПРОФИЛЬ. Опора не на
            // контекст допуска, а на право администрирования: у распорядителя допуска может ещё не
            // быть, и это нормально — иначе на чистом контуре выдать первый допуск было бы некому.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserClearanceRow>>.BadRequest(AdminGuard.Denied);
            }

            // Справочник отдаёт ВСЕ подразделения, включая недействующие: в допусках остаются номера
            // закрытых подразделений, и экран обязан показать их наименованием, а не «неизвестный номер».
            var directory = (await divisions.ListAsync(cancellationToken))
                .ToDictionary(d => d.Id, d => d.Name);

            var rows = (await clearances.ListAsync(cancellationToken))
                .Select(row => new UserClearanceRow(
                    row.UserId,
                    row.DisplayName,
                    row.MaxClassification,
                    [
                        .. row.DivisionScope.Select(id => new ClearanceDivision(
                            id, directory.TryGetValue(id, out var name) ? name : null)),
                    ]))
                .ToList();

            return ResponseDto<IReadOnlyList<UserClearanceRow>>.Ok(rows, rows.Count);
        }
    }
}

using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Clearances;

/// <summary>Выдать или заменить допуск пользователя (гриф + разрешённые подразделения).</summary>
public sealed record SetUserClearanceCommand(int UserId, short MaxClassification, IReadOnlyList<int> DivisionIds)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>
    /// В журнал идут ИМЕННО выданные границы: запись «кому какой доступ открыли» — то, ради чего
    /// журнал и ведётся (ТБ-030). Секретов тут нет — это параметры доступа, а не данные под грифом.
    /// </remarks>
    public string? AuditSummary =>
        $"admin:clearance:{UserId}:set:grif={MaxClassification};divisions={string.Join(',', DivisionIds)}";

    /// <inheritdoc />
    /// <remarks>
    /// Гриф записи журнала — не ниже ВЫДАВАЕМОГО грифа (ТБ-032): запись о выдаче допуска «совершенно
    /// секретно» не должна быть видна тому, кому такой гриф недоступен.
    /// </remarks>
    public short? AuditClassification => MaxClassification;

    /// <inheritdoc cref="SetUserClearanceCommand" />
    public sealed class Handler(IClearanceStore clearances, IPlatformAdministration administration)
        : IRequestHandler<SetUserClearanceCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetUserClearanceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ИНВАРИАНТ (ТБ-012/020): выдача допуска — распоряжение доступом, право даёт ПРОФИЛЬ.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.Denied);
            }

            return await clearances.SetAsync(
                command.UserId, command.MaxClassification, command.DivisionIds, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Активный пользователь с таким идентификатором не найден.");
        }
    }
}

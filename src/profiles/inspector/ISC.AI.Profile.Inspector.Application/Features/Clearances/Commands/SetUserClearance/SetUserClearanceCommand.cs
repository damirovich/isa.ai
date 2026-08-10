using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Clearances;

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
        $"inspector:clearance:{UserId}:set:grif={MaxClassification};divisions={string.Join(',', DivisionIds)}";

    /// <inheritdoc />
    /// <remarks>
    /// Гриф записи журнала — не ниже ВЫДАВАЕМОГО грифа (ТБ-032): запись о выдаче допуска «совершенно
    /// секретно» не должна быть видна тому, кому такой гриф недоступен.
    /// </remarks>
    public short? AuditClassification => MaxClassification;

    /// <inheritdoc cref="SetUserClearanceCommand" />
    public sealed class Handler(
        IClearanceStore clearances, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SetUserClearanceCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetUserClearanceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await ClearanceGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(ClearanceGuard.Denied);
            }

            return await clearances.SetAsync(
                command.UserId, command.MaxClassification, command.DivisionIds, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Активный пользователь с таким идентификатором не найден.");
        }
    }
}

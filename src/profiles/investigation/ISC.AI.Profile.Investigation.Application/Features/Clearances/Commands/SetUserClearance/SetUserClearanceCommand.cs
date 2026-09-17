using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Clearances;

/// <summary>Выдать или заменить допуск пользователя (гриф + разрешённые подразделения) (ТБ-011/020/021).</summary>
public sealed record SetUserClearanceCommand(int UserId, short MaxClassification, IReadOnlyList<int> DivisionIds)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>В журнал идут ИМЕННО выданные границы — «кому какой доступ открыли» (ТБ-030).</remarks>
    public string? AuditSummary =>
        $"investigation:clearance:{UserId}:set:grif={MaxClassification};divisions={string.Join(',', DivisionIds)}";

    /// <inheritdoc />
    /// <remarks>Гриф записи журнала — не ниже ВЫДАВАЕМОГО грифа (ТБ-032).</remarks>
    public short? AuditClassification => MaxClassification;

    /// <inheritdoc cref="SetUserClearanceCommand" />
    public sealed class Handler(IClearanceStore clearances, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SetUserClearanceCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetUserClearanceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.AdminDenied);
            }

            return await clearances.SetAsync(command.UserId, command.MaxClassification, command.DivisionIds, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Активный пользователь с таким идентификатором не найден.");
        }
    }
}

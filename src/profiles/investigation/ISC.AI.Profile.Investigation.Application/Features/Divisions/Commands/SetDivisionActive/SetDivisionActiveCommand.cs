using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Divisions;

/// <summary>Вывести подразделение из обращения или вернуть (ТФ-АДМ-01).</summary>
/// <remarks>
/// Отдельно от удаления: за расформированным подразделением числятся дела, носители и шаблоны, и их
/// владелец превратился бы в число без имени. Неактивное не предлагается при заведении дел и в допусках,
/// но история остаётся читаемой.
/// </remarks>
public sealed record SetDivisionActiveCommand(int Id, bool IsActive) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:division:{Id}:{(IsActive ? "enable" : "disable")}";

    /// <inheritdoc cref="SetDivisionActiveCommand" />
    public sealed class Handler(IDivisionAdminStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SetDivisionActiveCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetDivisionActiveCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.AdminDenied);
            }

            var result = await store.SetActiveAsync(command.Id, command.IsActive, cancellationToken);
            return result == DivisionWriteResult.Ok
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Подразделение не найдено.");
        }
    }
}

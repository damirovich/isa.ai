using System.Globalization;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Изменить реквизиты дела (ТФ-ДЕЛ-01). Гриф и подразделение НЕ меняются: их уже унаследовали носители,
/// фигуранты и шаблоны (ТБ-070) — иначе производные разошлись бы с делом. Реквизиты задания
/// (<paramref name="TaskRequisites"/>, ТФ-ДЕЛ-05) заменяются целиком; при смене вида с задания — очищаются.
/// </summary>
public sealed record UpdateCaseCommand(
    int CaseId,
    string Title,
    CaseKind Kind,
    DateOnly OpenedAt,
    int? InvestigatorUserId,
    string? Basis = null,
    TaskRequisites? TaskRequisites = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Из реквизитов задания в сводку идёт только идентификатор ГУ-инициатора (ТБ-032).</remarks>
    public string? AuditSummary =>
        $"investigation:case:{CaseId}:update:kind={Kind};investigator={InvestigatorUserId?.ToString(CultureInfo.InvariantCulture) ?? "-"}"
        + (TaskRequisites is { } task ? $";initiator={task.InitiatorUnitId.ToString(CultureInfo.InvariantCulture)}" : string.Empty);

    /// <inheritdoc cref="UpdateCaseCommand" />
    public sealed class Handler(
        ICaseStore cases, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<UpdateCaseCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(UpdateCaseCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await cases.UpdateAsync(
                command.CaseId, command.Title.Trim(), command.Kind, command.OpenedAt, command.InvestigatorUserId,
                string.IsNullOrWhiteSpace(command.Basis) ? null : command.Basis.Trim(),
                command.TaskRequisites, access, cancellationToken);
            return CaseGuard.ToResponse(result);
        }
    }
}

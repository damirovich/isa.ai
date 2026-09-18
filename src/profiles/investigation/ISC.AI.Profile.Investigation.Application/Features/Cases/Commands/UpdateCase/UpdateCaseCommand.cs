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
/// фигуранты и шаблоны (ТБ-070) — иначе производные разошлись бы с делом.
/// </summary>
public sealed record UpdateCaseCommand(
    int CaseId,
    string Title,
    CaseKind Kind,
    DateOnly OpenedAt,
    int? InvestigatorUserId,
    string? Basis = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"investigation:case:{CaseId}:update:kind={Kind};investigator={InvestigatorUserId?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

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
                access, cancellationToken);
            return CaseGuard.ToResponse(result);
        }
    }
}

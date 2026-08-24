using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Violations;

/// <summary>Править нарушение целиком (включая статус устранения — им живёт светофор риска).</summary>
public sealed record UpdateViolationCommand(
    int ViolationId,
    int DivisionId,
    int CategoryId,
    ViolationSeverity Severity,
    DateOnly DetectedAt,
    RemediationStatus RemediationStatus,
    string? SourceDocRef = null,
    string? SourceAssignmentRef = null,
    string? ReferenceDocRef = null,
    string? Cause = null,
    string? Recommendation = null) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"inspector:violation:{ViolationId}:update:remediation={RemediationStatus}";

    /// <inheritdoc cref="UpdateViolationCommand" />
    public sealed class Handler(IViolationStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<UpdateViolationCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UpdateViolationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await ViolationGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(ViolationGuard.Denied);
            }

            var draft = new ViolationDraft(
                command.DivisionId, command.CategoryId, command.Severity, command.DetectedAt,
                command.RemediationStatus, command.SourceDocRef, command.SourceAssignmentRef,
                command.ReferenceDocRef, command.Cause, command.Recommendation);
            var result = await store.UpdateAsync(command.ViolationId, draft, cancellationToken);
            return result switch
            {
                ViolationWriteResult.Ok => ResponseDto<bool>.Ok(true),
                ViolationWriteResult.CategoryNotLeaf =>
                    ResponseDto<bool>.BadRequest("Выберите ВИД нарушения внутри сферы — сфера целиком не категория факта."),
                _ => ResponseDto<bool>.NotFound("Нарушение, подразделение или вид не найдены."),
            };
        }
    }
}

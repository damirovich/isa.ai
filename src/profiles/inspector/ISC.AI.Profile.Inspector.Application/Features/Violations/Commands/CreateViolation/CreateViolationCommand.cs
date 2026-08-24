using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Violations;

/// <summary>Занести нарушение (Э5-01, Приложение §4). Заносит ЧЕЛОВЕК; ИИ здесь ничего не считает.</summary>
public sealed record CreateViolationCommand(
    int DivisionId,
    int CategoryId,
    ViolationSeverity Severity,
    DateOnly DetectedAt,
    RemediationStatus RemediationStatus,
    string? SourceDocRef = null,
    string? SourceAssignmentRef = null,
    string? ReferenceDocRef = null,
    string? Cause = null,
    string? Recommendation = null,
    DateOnly? RemediationDeadline = null) : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"inspector:violation:create:division={DivisionId};category={CategoryId};severity={Severity}";

    /// <inheritdoc cref="CreateViolationCommand" />
    public sealed class Handler(IViolationStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<CreateViolationCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            CreateViolationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await ViolationGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(ViolationGuard.Denied);
            }

            var (result, violationId) = await store.CreateAsync(Draft(command), cancellationToken);
            return result switch
            {
                ViolationWriteResult.Ok => ResponseDto<int>.Ok(violationId),
                _ => ResponseDto<int>.NotFound("Подразделение или категория нарушения не найдены."),
            };
        }
    }

    internal static ViolationDraft Draft(CreateViolationCommand c) => new(
        c.DivisionId, c.CategoryId, c.Severity, c.DetectedAt, c.RemediationStatus,
        c.SourceDocRef, c.SourceAssignmentRef, c.ReferenceDocRef, c.Cause, c.Recommendation,
        c.RemediationDeadline);
}

using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>Утвердить методику (<paramref name="Approve"/>) или вернуть в черновики.</summary>
public sealed record SetMethodDocumentStatusCommand(int MethodId, bool Approve)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:method:{MethodId}:{(Approve ? "approve" : "unapprove")}";

    /// <inheritdoc cref="SetMethodDocumentStatusCommand" />
    public sealed class Handler(
        IMethodRegistryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider,
        IAccessContextProvider accessContextProvider)
        : IRequestHandler<SetMethodDocumentStatusCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetMethodDocumentStatusCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await MethodRegistryGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(MethodRegistryGuard.ManageDenied);
            }

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
            var result = await store.SetStatusAsync(
                command.MethodId,
                command.Approve ? MethodDocumentStatus.Approved : MethodDocumentStatus.Draft,
                command.Approve ? await subjectProvider.GetCurrentUserIdAsync(cancellationToken) : null,
                access.MaxClassification,
                cancellationToken);
            return result == MethodWriteResult.Ok
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Методика не найдена.");
        }
    }
}

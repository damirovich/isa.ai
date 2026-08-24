using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>Удалить ЧЕРНОВИК методики. Утверждённая — институциональная память, не удаляется.</summary>
public sealed record DeleteMethodDocumentCommand(int MethodId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:method:{MethodId}:delete";

    /// <inheritdoc cref="DeleteMethodDocumentCommand" />
    public sealed class Handler(
        IMethodRegistryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider,
        IAccessContextProvider accessContextProvider)
        : IRequestHandler<DeleteMethodDocumentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            DeleteMethodDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await MethodRegistryGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(MethodRegistryGuard.ManageDenied);
            }

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
            var result = await store.DeleteAsync(command.MethodId, access.MaxClassification, cancellationToken);
            return result switch
            {
                MethodWriteResult.Ok => ResponseDto<bool>.Ok(true),
                MethodWriteResult.NotDraft =>
                    ResponseDto<bool>.Conflict("Утверждённая методика не удаляется — верните в черновики."),
                _ => ResponseDto<bool>.NotFound("Методика не найдена."),
            };
        }
    }
}

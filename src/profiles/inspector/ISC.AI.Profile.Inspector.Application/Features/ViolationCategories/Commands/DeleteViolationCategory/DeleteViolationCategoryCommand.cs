using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.ViolationCategories;

/// <summary>Удалить элемент классификатора — только пустой (без нарушений и дочерних видов).</summary>
public sealed record DeleteViolationCategoryCommand(int CategoryId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:violation-category:{CategoryId}:delete";

    /// <inheritdoc cref="DeleteViolationCategoryCommand" />
    public sealed class Handler(IViolationCategoryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<DeleteViolationCategoryCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            DeleteViolationCategoryCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(SaveViolationCategoryCommand.Handler.Denied);
            }

            var result = await store.DeleteAsync(command.CategoryId, cancellationToken);
            return result switch
            {
                ViolationWriteResult.Ok => ResponseDto<bool>.Ok(true),
                ViolationWriteResult.InUse =>
                    ResponseDto<bool>.Conflict("За элементом числятся нарушения или виды — удаление невозможно."),
                _ => ResponseDto<bool>.NotFound("Элемент классификатора не найден."),
            };
        }
    }
}

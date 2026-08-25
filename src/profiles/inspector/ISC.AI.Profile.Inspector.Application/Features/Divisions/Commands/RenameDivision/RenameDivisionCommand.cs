using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Divisions;

/// <summary>Переименовать подразделение / изменить код сопоставления со СКИД и тип (§4.2).</summary>
public sealed record RenameDivisionCommand(
    int Id, string Name, string? Code,
    Domain.Enums.DivisionKind Kind = Domain.Enums.DivisionKind.Territorial)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:division:rename:{Id}";

    /// <inheritdoc cref="RenameDivisionCommand" />
    public sealed class Handler(IDivisionAdminStore store) : IRequestHandler<RenameDivisionCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            RenameDivisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var found = await store.RenameAsync(
                command.Id, command.Name, command.Code, command.Kind, cancellationToken);
            return found ? ResponseDto<bool>.Ok(true) : ResponseDto<bool>.NotFound("Подразделение не найдено.");
        }
    }
}

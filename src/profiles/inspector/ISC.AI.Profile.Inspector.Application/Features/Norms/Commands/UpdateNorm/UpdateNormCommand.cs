using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>
/// Править номер и название нормы. Правка номера — редкая и осторожная операция: на номер ссылается
/// грунтовка, и переименование меняет то, как справки находят эту норму.
/// </summary>
public sealed record UpdateNormCommand(int NormId, string Identifier, string Title)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:norm:{NormId}:update:{Identifier}";

    /// <inheritdoc cref="UpdateNormCommand" />
    public sealed class Handler(INormRegistryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<UpdateNormCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(UpdateNormCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await NormGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(NormGuard.Denied);
            }

            var result = await store.UpdateAsync(command.NormId, command.Identifier, command.Title, cancellationToken);
            return result switch
            {
                NormWriteResult.Ok => ResponseDto<bool>.Ok(true),
                NormWriteResult.NotFound => ResponseDto<bool>.NotFound("Норма не найдена."),
                NormWriteResult.DuplicateIdentifier =>
                    ResponseDto<bool>.Conflict($"Норма с номером «{command.Identifier.Trim()}» уже есть в картотеке."),
                _ => ResponseDto<bool>.BadRequest("Не удалось сохранить норму."),
            };
        }
    }
}

using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>Создать норму в картотеке (ТФ-НПА-02). Номер уникален — на него ссылается грунтовка.</summary>
public sealed record CreateNormCommand(string Identifier, string Title)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:norm:create:{Identifier}";

    /// <inheritdoc cref="CreateNormCommand" />
    public sealed class Handler(INormRegistryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<CreateNormCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(CreateNormCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await NormGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(NormGuard.Denied);
            }

            var (result, normId) = await store.CreateAsync(command.Identifier, command.Title, cancellationToken);
            return result switch
            {
                NormWriteResult.Ok => ResponseDto<int>.Ok(normId),
                NormWriteResult.DuplicateIdentifier =>
                    ResponseDto<int>.Conflict($"Норма с номером «{command.Identifier.Trim()}» уже есть в картотеке."),
                _ => ResponseDto<int>.BadRequest("Не удалось создать норму."),
            };
        }
    }
}

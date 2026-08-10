using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Roles;

/// <summary>Назначить роль пользователю (<paramref name="Role"/> = <see langword="null"/> — снять роль).</summary>
public sealed record SetUserRoleCommand(int UserId, UserRole? Role) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:user-role:{UserId}:{(Role is { } r ? r.ToString() : "снята")}";

    /// <inheritdoc cref="SetUserRoleCommand" />
    public sealed class Handler(IUserRoleStore store, ISubjectProvider subjectProvider)
        : IRequestHandler<SetUserRoleCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetUserRoleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleScenariosGuard.CallerCanManageRolesAsync(store, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest("Назначение ролей доступно только Администратору.");
            }

            await store.SetRoleAsync(command.UserId, command.Role, cancellationToken);
            return ResponseDto<bool>.Ok(true);
        }
    }
}

using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.References;

/// <summary>Вывести запись справочника из обращения или вернуть (ТФ-АДМ-07).</summary>
/// <param name="Id">Запись.</param>
/// <param name="IsActive">Действующая.</param>
/// <remarks>
/// Вместо удаления: на запись ссылаются задания, и их инициатор превратился бы в число без имени.
/// Выключенная запись не предлагается в новых заданиях, но остаётся в старых и при их правке.
/// </remarks>
public sealed record SetReferenceItemActiveCommand(int Id, bool IsActive) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:reference:{Id}:{(IsActive ? "enable" : "disable")}";

    /// <inheritdoc cref="SetReferenceItemActiveCommand" />
    public sealed class Handler(IReferenceStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SetReferenceItemActiveCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetReferenceItemActiveCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.AdminDenied);
            }

            return ReferenceGuard.ToResponse(await store.SetActiveAsync(command.Id, command.IsActive, cancellationToken));
        }
    }
}

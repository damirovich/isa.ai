using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Roles;

/// <summary>Назначить роль пользователю (<paramref name="Role"/> = <see langword="null"/> — снять роль) (ТП-004).</summary>
public sealed record SetUserRoleCommand(int UserId, InvestigationRole? Role) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <summary>Отказ снять роль с последнего Администратора.</summary>
    public const string LastAdministrator =
        "Нельзя снять роль с последнего Администратора: система осталась бы без управления ролями и допусками.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:user-role:{UserId}:{(Role is { } r ? r.ToString() : "снята")}";

    /// <inheritdoc cref="SetUserRoleCommand" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SetUserRoleCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetUserRoleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.AdminDenied);
            }

            // Последний Администратор неснимаем: иначе окно первичной настройки открылось бы заново для
            // ЛЮБОГО вошедшего (самовосстановление правила обернулось бы дырой), а до этого — «замок без ключа».
            if (command.Role != InvestigationRole.Administrator
                && await roles.GetRoleAsync(command.UserId, cancellationToken) == InvestigationRole.Administrator)
            {
                var administrators = await roles.ListUserIdsByRoleAsync(InvestigationRole.Administrator, cancellationToken);
                if (administrators.Count <= 1)
                {
                    return ResponseDto<bool>.BadRequest(LastAdministrator);
                }
            }

            await roles.SetRoleAsync(command.UserId, command.Role, cancellationToken);
            return ResponseDto<bool>.Ok(true);
        }
    }
}

/// <summary>Правила назначения роли.</summary>
public sealed class SetUserRoleValidator : AbstractValidator<SetUserRoleCommand>
{
    /// <summary>Пользователь положительный; роль — из перечисления либо отсутствует.</summary>
    public SetUserRoleValidator()
    {
        RuleFor(c => c.UserId).GreaterThan(0);
        RuleFor(c => c.Role).IsInEnum().When(c => c.Role is not null).WithMessage("Неизвестная роль.");
    }
}

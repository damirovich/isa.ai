using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Roles;

/// <summary>
/// Назначить сотруднику роль профиля с карточки на экране «Пользователи» (ТП-004/007); <paramref name="RoleKey"/> =
/// <see langword="null"/> — снять роль.
/// </summary>
/// <param name="UserId">Пользователь ядра.</param>
/// <param name="RoleKey">Непрозрачный ключ роли из <see cref="IUserRoleCatalog.ListRolesAsync"/>.</param>
public sealed record AssignUserRoleCommand(int UserId, string? RoleKey) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <summary>Маркер снятой роли в сводке журнала.</summary>
    public const string RemovedMarker = "снята";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Формат «admin:role:{пользователь}:{ключ}» разбирает история карточки сотрудника (<see cref="Accounts.UserHistoryText"/>).</remarks>
    public string? AuditSummary => $"admin:role:{UserId}:{RoleKey ?? RemovedMarker}";

    /// <inheritdoc cref="AssignUserRoleCommand" />
    public sealed class Handler(IUserRoleCatalog roles, IPlatformAdministration administration)
        : IRequestHandler<AssignUserRoleCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(AssignUserRoleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ИНВАРИАНТ (ТБ-012): назначать роли — право администрирования, его знает профиль. Инварианты самой модели
            // ролей (неизвестный ключ, последний Администратор) проверяет порт профиля.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.Denied);
            }

            var result = await roles.AssignAsync(command.UserId, command.RoleKey, cancellationToken);
            return result.Succeeded
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.BadRequest(result.Error ?? "Роль не назначена.");
        }
    }
}

/// <summary>Проверка формы назначения роли.</summary>
public sealed class AssignUserRoleValidator : AbstractValidator<AssignUserRoleCommand>
{
    /// <summary>Правила.</summary>
    public AssignUserRoleValidator()
    {
        RuleFor(c => c.UserId).GreaterThan(0);
        RuleFor(c => c.RoleKey).MaximumLength(100);
    }
}

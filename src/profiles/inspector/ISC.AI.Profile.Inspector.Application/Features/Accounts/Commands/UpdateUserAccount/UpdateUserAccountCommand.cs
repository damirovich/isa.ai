using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Accounts;

/// <summary>Изменить справочные поля учётной записи: ФИО и должность.</summary>
/// <remarks>
/// Роль и допуск здесь НЕ меняются — у них свои экраны и свои записи в журнале. Общая форма
/// «поменять всё сразу», как в СКИД, склеивает разные полномочия в одно действие: администратор,
/// исправивший опечатку в фамилии, незаметно для себя переутверждает и права.
/// </remarks>
public sealed record UpdateUserAccountCommand(int UserId, string? DisplayName, string? Position)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:account:{UserId}:update-profile";

    /// <inheritdoc cref="UpdateUserAccountCommand" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<UpdateUserAccountCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UpdateUserAccountCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AccountGuard.Denied);
            }

            return await accounts.UpdateProfileAsync(
                command.UserId, command.DisplayName, command.Position, cancellationToken)
                ? ResponseDto<bool>.Ok(true, "Сохранено.")
                : ResponseDto<bool>.NotFound("Учётная запись не найдена.");
        }
    }
}

using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Accounts;

/// <summary>
/// Ведение учётных записей и смена пароля (Э4-35 §6.5, шаг 3). После перехода на локальную
/// идентичность это единственное место, где заводят доступ и восстанавливают забытый пароль:
/// внешней системы, куда можно было отослать пользователя, больше нет.
/// </summary>
/// <remarks>
/// ПАРОЛИ НИКОГДА НЕ ПОПАДАЮТ В ЖУРНАЛ (ТБ-043): в <c>AuditSummary</c> уходит только факт и субъект.
/// Временный пароль показывается администратору ОДИН раз в ответе команды и нигде не сохраняется
/// в открытом виде.
/// </remarks>

/// <summary>Все учётные записи (экран администрирования).</summary>
public sealed record ListUserAccountsQuery : IRequest<ResponseDto<IReadOnlyList<UserAccountRow>>>
{
    /// <inheritdoc cref="ListUserAccountsQuery" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserAccountsQuery, ResponseDto<IReadOnlyList<UserAccountRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserAccountRow>>> Handle(
            ListUserAccountsQuery query, CancellationToken cancellationToken)
        {
            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserAccountRow>>.BadRequest(AccountGuard.Denied);
            }

            var items = await accounts.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserAccountRow>>.Ok(items, items.Count);
        }
    }
}

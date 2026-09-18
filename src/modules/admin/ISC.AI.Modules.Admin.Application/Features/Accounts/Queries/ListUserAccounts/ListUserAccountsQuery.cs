using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>
/// Все учётные записи (экран администрирования). После перехода на локальную идентичность это
/// единственное место, где заводят доступ и восстанавливают забытый пароль: внешней системы, куда
/// можно было отослать пользователя, больше нет.
/// </summary>
/// <remarks>
/// ПАРОЛИ НИКОГДА НЕ ПОПАДАЮТ В ЖУРНАЛ (ТБ-043): в <c>AuditSummary</c> команд уходит только факт и
/// субъект. Временный пароль показывается администратору ОДИН раз в ответе команды и нигде не
/// сохраняется в открытом виде.
/// </remarks>
public sealed record ListUserAccountsQuery : IRequest<ResponseDto<IReadOnlyList<UserAccountRow>>>
{
    /// <inheritdoc cref="ListUserAccountsQuery" />
    public sealed class Handler(IUserAccountStore accounts, IPlatformAdministration administration)
        : IRequestHandler<ListUserAccountsQuery, ResponseDto<IReadOnlyList<UserAccountRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserAccountRow>>> Handle(
            ListUserAccountsQuery query, CancellationToken cancellationToken)
        {
            // ИНВАРИАНТ (ТБ-012): перечень учётных записей — режимные сведения, право даёт профиль.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserAccountRow>>.BadRequest(AdminGuard.Denied);
            }

            var items = await accounts.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserAccountRow>>.Ok(items, items.Count);
        }
    }
}

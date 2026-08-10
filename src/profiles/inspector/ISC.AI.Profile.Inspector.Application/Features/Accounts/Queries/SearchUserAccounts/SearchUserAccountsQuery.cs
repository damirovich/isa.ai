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
/// Постраничный поиск учётных записей с фильтрами (роль, подразделение, состояние) и текстом.
/// </summary>
/// <remarks>
/// Фильтр ПО РОЛИ обрабатывается здесь, а не в ядре: роли ведёт профиль в своей схеме, ядро о них
/// не знает, а соединить две схемы одним запросом через два разных контекста нельзя. Профиль
/// превращает роль в набор идентификаторов и отдаёт его ядру — постраничность при этом остаётся
/// серверной, а ядро не узнаёт слова «роль».
/// </remarks>
public sealed record SearchUserAccountsQuery(
    string? Text = null,
    UserRole? Role = null,
    int? DivisionId = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 25) : IRequest<ResponseDto<UserAccountViewPage>>
{
    /// <inheritdoc cref="SearchUserAccountsQuery" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SearchUserAccountsQuery, ResponseDto<UserAccountViewPage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<UserAccountViewPage>> Handle(
            SearchUserAccountsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<UserAccountViewPage>.BadRequest(AccountGuard.Denied);
            }

            // Реестр ролей — таблица размером с штат организации, её чтение целиком дешевле, чем
            // попытка соединить схемы. Он же даёт роль для каждой строки выдачи.
            var roleRows = await roles.ListAsync(cancellationToken);
            var roleByUser = roleRows
                .GroupBy(r => r.UserId)
                .ToDictionary(g => g.Key, g => g.First().Role);

            IReadOnlyList<int>? restrict = null;
            if (query.Role is { } role)
            {
                // Пустой набор — законный исход «под эту роль никого нет»; ядро понимает его именно
                // так и вернёт пустую страницу, а не весь список.
                restrict = [.. roleRows.Where(r => r.Role == role).Select(r => r.UserId)];
            }

            var page = await accounts.SearchAsync(
                new UserAccountFilter(
                    query.Text, query.IsActive, query.DivisionId, restrict, query.Page, query.PageSize),
                cancellationToken);

            var rows = page.Rows
                .Select(row => new UserAccountView(
                    row, roleByUser.TryGetValue(row.UserId, out var value) ? value : null))
                .ToList();

            return ResponseDto<UserAccountViewPage>.Ok(
                new UserAccountViewPage(rows, page.TotalCount), page.TotalCount);
        }
    }
}

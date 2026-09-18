using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>
/// Постраничный поиск учётных записей с фильтрами (роль, подразделение, состояние) и текстом.
/// </summary>
/// <param name="Text">Поиск по имени входа, ФИО и должности (без учёта регистра).</param>
/// <param name="RoleKey">
/// Ключ роли из <see cref="IUserRoleCatalog"/>; <see langword="null"/> — без фильтра по роли.
/// Ключ НЕПРОЗРАЧНЫЙ: пакет им только сравнивает, смысла роли не знает (ролей у каждого профиля свои).
/// </param>
/// <param name="DivisionId">Подразделение из допуска пользователя.</param>
/// <param name="IsActive">Только включённые/только отключённые; <see langword="null"/> — все.</param>
/// <param name="Page">Номер страницы, с 1.</param>
/// <param name="PageSize">Размер страницы.</param>
/// <remarks>
/// Фильтр ПО РОЛИ обрабатывается здесь, а не в ядре: роли ведёт ПРОФИЛЬ в своей схеме, ядро о них
/// не знает, а соединить две схемы одним запросом через два разных контекста нельзя. Поэтому роль
/// превращается в набор идентификаторов и отдаётся ядру через <c>UserAccountFilter.RestrictToUserIds</c>
/// — постраничность при этом остаётся серверной, а ядро не узнаёт слова «роль».
/// </remarks>
public sealed record SearchUserAccountsQuery(
    string? Text = null,
    string? RoleKey = null,
    int? DivisionId = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 25) : IRequest<ResponseDto<UserAccountViewPage>>
{
    /// <inheritdoc cref="SearchUserAccountsQuery" />
    public sealed class Handler(
        IUserAccountStore accounts,
        IUserRoleCatalog roleCatalog,
        IPlatformAdministration administration)
        : IRequestHandler<SearchUserAccountsQuery, ResponseDto<UserAccountViewPage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<UserAccountViewPage>> Handle(
            SearchUserAccountsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // ИНВАРИАНТ (ТБ-012): перечень учётных записей — режимные сведения, право даёт профиль.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<UserAccountViewPage>.BadRequest(AdminGuard.Denied);
            }

            // Назначения ролей — таблица размером со штат организации, её чтение целиком дешевле,
            // чем попытка соединить схемы. Она же даёт роль для каждой строки выдачи.
            var roleKeyByUser = await roleCatalog.GetUserRoleKeysAsync(cancellationToken);
            var labelByKey = (await roleCatalog.ListRolesAsync(cancellationToken))
                .ToDictionary(r => r.Key, r => r.Label, StringComparer.Ordinal);

            IReadOnlyList<int>? restrict = null;
            if (!string.IsNullOrWhiteSpace(query.RoleKey))
            {
                // Пустой набор — законный исход «под эту роль никого нет»; ядро понимает его именно
                // так и вернёт пустую страницу, а не весь список.
                restrict =
                [
                    .. roleKeyByUser
                        .Where(pair => string.Equals(pair.Value, query.RoleKey, StringComparison.Ordinal))
                        .Select(pair => pair.Key),
                ];
            }

            var page = await accounts.SearchAsync(
                new UserAccountFilter(
                    query.Text, query.IsActive, query.DivisionId, restrict, query.Page, query.PageSize),
                cancellationToken);

            var rows = page.Rows
                .Select(row =>
                {
                    var key = roleKeyByUser.TryGetValue(row.UserId, out var value) ? value : null;
                    var label = key is not null && labelByKey.TryGetValue(key, out var text) ? text : null;
                    return new UserAccountView(row, key, label);
                })
                .ToList();

            return ResponseDto<UserAccountViewPage>.Ok(
                new UserAccountViewPage(rows, page.TotalCount), page.TotalCount);
        }
    }
}

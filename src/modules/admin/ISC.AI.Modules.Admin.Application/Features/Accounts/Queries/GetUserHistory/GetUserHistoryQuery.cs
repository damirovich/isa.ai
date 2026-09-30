using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>
/// История изменений по сотруднику для его карточки: учётная запись, роль, допуск — из неизменяемого журнала
/// аудита (ТБ-030), новые первыми.
/// </summary>
/// <param name="UserId">Сотрудник.</param>
public sealed record GetUserHistoryQuery(int UserId) : IRequest<ResponseDto<IReadOnlyList<UserHistoryEntry>>>, IAuditableRequest
{
    /// <summary>Сколько событий показывает карточка; полная история — в журнале аудита.</summary>
    public const int MaxEntries = 30;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    /// <remarks>Не «admin:account:{id}:…» — иначе просмотр истории сам попадал бы в историю.</remarks>
    public string? AuditSummary => $"admin:user-history:{UserId}:view";

    /// <inheritdoc cref="GetUserHistoryQuery" />
    public sealed class Handler(
        IAuditReader reader,
        IAccessContextProvider accessProvider,
        IUserAccountStore accounts,
        IUserRoleCatalog roleCatalog,
        IDivisionCatalog divisions,
        IPlatformAdministration administration)
        : IRequestHandler<GetUserHistoryQuery, ResponseDto<IReadOnlyList<UserHistoryEntry>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserHistoryEntry>>> Handle(
            GetUserHistoryQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // ИНВАРИАНТ (ТБ-030/032): история — выборка из журнала, право открыть журнал даёт профиль отдельно
            // от права администрировать.
            if (!await administration.CanViewAuditAsync(cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserHistoryEntry>>.BadRequest(AdminGuard.AuditDenied);
            }

            var page = await accounts.SearchAsync(
                new UserAccountFilter(RestrictToUserIds: [query.UserId], Page: 1, PageSize: 1), cancellationToken);
            if (page.Rows.FirstOrDefault(r => r.UserId == query.UserId) is not { } account)
            {
                return ResponseDto<IReadOnlyList<UserHistoryEntry>>.NotFound("Учётная запись не найдена.");
            }

            AccessContext access;
            try
            {
                access = await accessProvider.GetCurrentAsync(cancellationToken);
            }
            catch (AccessContextRequiredException)
            {
                // FAIL-CLOSED (ТБ-012/021): без допуска журнал не читается — объясняем, а не падаем.
                return ResponseDto<IReadOnlyList<UserHistoryEntry>>.BadRequest(AdminGuard.AuditClearanceRequired);
            }

            var id = query.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AuditFilter[] needles =
            [
                new(Action: AuditAction.Modify, ObjectRef: $"admin:account:{id}:"),
                new(Action: AuditAction.Modify, ObjectRef: $"admin:clearance:{id}:"),
                new(Action: AuditAction.Modify, ObjectRef: $"admin:role:{id}:"),
                new(Action: AuditAction.Modify, ObjectRef: $":user-role:{id}:"),
                new(Action: AuditAction.Modify, ObjectRef: "admin:account:create:" + account.UserName),
                new(SubjectId: query.UserId, Action: AuditAction.Modify, ObjectRef: UserHistoryText.OwnPasswordChange),
            ];

            // Режимная часть — в порту журнала: записи выше допуска смотрящего не выдаются (ТБ-032).
            var records = new Dictionary<long, AuditRecordRow>();
            foreach (var needle in needles)
            {
                var found = await reader.QueryAsync(needle with { Page = 1, PageSize = MaxEntries }, access, cancellationToken);
                foreach (var row in found.Rows)
                {
                    records.TryAdd(row.Id, row);
                }
            }

            var roleLabels = (await roleCatalog.ListRolesAsync(cancellationToken))
                .ToDictionary(r => r.Key, r => r.Label, StringComparer.Ordinal);
            var divisionNames = (await divisions.ListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);

            IReadOnlyList<UserHistoryEntry> entries =
            [
                .. records.Values
                    .OrderByDescending(r => r.OccurredAt)
                    .ThenByDescending(r => r.Id)
                    .Select(r => (Row: r, Text: UserHistoryText.Describe(
                        r.ObjectRef, r.SubjectId, query.UserId, account.UserName, roleLabels, divisionNames)))
                    .Where(x => x.Text is not null)
                    .Take(MaxEntries)
                    .Select(x => new UserHistoryEntry(x.Row.OccurredAt, x.Row.SubjectName, x.Text!.Value.What, x.Text.Value.Kind)),
            ];
            return ResponseDto<IReadOnlyList<UserHistoryEntry>>.Ok(entries, entries.Count);
        }
    }
}

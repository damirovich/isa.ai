using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Accounts;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Audit;

/// <summary>
/// Просмотр неизменяемого журнала аудита (ТБ-030/032).
/// </summary>
/// <remarks>
/// Сценарий живёт в ПРОФИЛЕ, а не в ядре: право просмотра определяется РОЛЬЮ, а роли ведёт профиль
/// (то же разделение, что у учётных записей и допусков). Ядро отдаёт нейтральный порт
/// <see cref="IAuditReader"/>, который сам по себе решает только режимную часть — что субъекту видно
/// по грифу и подразделению.
/// </remarks>
public sealed record ListAuditRecordsQuery(AuditFilter Filter)
    : IRequest<ResponseDto<AuditPage>>, IAuditableRequest
{
    /// <inheritdoc />
    /// <remarks>
    /// Чтение журнала САМО пишется в журнал: «кто смотрел историю» — такое же обращение к режимным
    /// данным, как просмотр документа, и оставлять его без следа нельзя. Рекурсии тут нет: запись
    /// о просмотре ничего не читает.
    /// </remarks>
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "core:audit:view";

    /// <inheritdoc cref="ListAuditRecordsQuery" />
    public sealed class Handler(
        IAuditReader reader,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider)
        : IRequestHandler<ListAuditRecordsQuery, ResponseDto<AuditPage>>
    {
        /// <summary>Единый текст отказа — тот же смысл, что у ведения учётных записей.</summary>
        public const string Denied = "Просмотр журнала аудита доступен только Администратору.";

        /// <inheritdoc />
        public async ValueTask<ResponseDto<AuditPage>> Handle(
            ListAuditRecordsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Право просмотра — по роли. Правило то же, что у учётных записей (AccountGuard):
            // Администратор всегда; любой вошедший — только пока Администратора в системе нет
            // (иначе после чистого развёртывания журнал был бы недоступен вообще никому).
            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<AuditPage>.BadRequest(Denied);
            }

            // Режимная часть — в порту: даже Администратор не видит записей выше своего допуска
            // (ТБ-032). Роль даёт право ОТКРЫТЬ журнал, а не право видеть в нём всё.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var page = await reader.QueryAsync(query.Filter, access, cancellationToken);

            return ResponseDto<AuditPage>.Ok(page, page.TotalCount);
        }
    }
}

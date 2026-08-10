using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Notifications;

/// <summary>
/// Сценарии ленты уведомлений (разд. 5 ТЗ СКИД). Адресат берётся ИЗ КОНТЕКСТА ДОПУСКА, а не из
/// параметров запроса: иначе «покажи уведомления пользователя N» стало бы чтением чужой ленты.
/// </summary>

/// <summary>Непрочитанные уведомления текущего пользователя (новые первыми).</summary>
/// <remarks>
/// НЕ помечается <c>IAuditableRequest</c> НАМЕРЕННО: колокольчик опрашивается регулярно, и каждая
/// выборка своей же ленты забивала бы неизменяемый журнал (ТБ-030) шумом, в котором утонут настоящие
/// обращения к документам. Сами документы читаются отдельными сценариями — они аудируются.
/// </remarks>
public sealed record ListNotificationsQuery(int MaxCount = 20)
    : IRequest<ResponseDto<IReadOnlyList<NotificationItem>>>
{
    /// <inheritdoc cref="ListNotificationsQuery" />
    public sealed class Handler(INotificationStore notifications, IAccessContextProvider accessProvider)
        : IRequestHandler<ListNotificationsQuery, ResponseDto<IReadOnlyList<NotificationItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<NotificationItem>>> Handle(
            ListNotificationsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var items = await notifications.ListUnreadAsync(access, query.MaxCount, cancellationToken);
            return ResponseDto<IReadOnlyList<NotificationItem>>.Ok(items, items.Count);
        }
    }
}

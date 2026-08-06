using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Notifications;

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

/// <summary>Число непрочитанных уведомлений текущего пользователя (значок колокольчика).</summary>
public sealed record CountUnreadNotificationsQuery : IRequest<ResponseDto<int>>
{
    /// <inheritdoc cref="CountUnreadNotificationsQuery" />
    public sealed class Handler(INotificationStore notifications, IAccessContextProvider accessProvider)
        : IRequestHandler<CountUnreadNotificationsQuery, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            CountUnreadNotificationsQuery query, CancellationToken cancellationToken)
        {
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return ResponseDto<int>.Ok(await notifications.CountUnreadAsync(access, cancellationToken));
        }
    }
}

/// <summary>Пометить уведомление прочитанным.</summary>
public sealed record MarkNotificationReadCommand(int NotificationId) : IRequest<ResponseDto<bool>>
{
    /// <inheritdoc cref="MarkNotificationReadCommand" />
    public sealed class Handler(INotificationStore notifications, IAccessContextProvider accessProvider)
        : IRequestHandler<MarkNotificationReadCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            MarkNotificationReadCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } recipientUserId)
            {
                return ResponseDto<bool>.BadRequest("Действие требует аутентифицированного пользователя.");
            }

            // Чужое уведомление неотличимо от несуществующего — тот же ответ, что и «нет такого».
            return await notifications.MarkReadAsync(command.NotificationId, recipientUserId, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Уведомление не найдено.");
        }
    }
}

/// <summary>Пометить прочитанными все свои уведомления.</summary>
public sealed record MarkAllNotificationsReadCommand : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Массовое действие, в отличие от точечной пометки, — в журнал попадает (ТБ-030).</remarks>
    public string? AuditSummary => "docflow:notifications:mark-all-read";

    /// <inheritdoc cref="MarkAllNotificationsReadCommand" />
    public sealed class Handler(INotificationStore notifications, IAccessContextProvider accessProvider)
        : IRequestHandler<MarkAllNotificationsReadCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            MarkAllNotificationsReadCommand command, CancellationToken cancellationToken)
        {
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } recipientUserId)
            {
                return ResponseDto<bool>.BadRequest("Действие требует аутентифицированного пользователя.");
            }

            await notifications.MarkAllReadAsync(recipientUserId, cancellationToken);
            return ResponseDto<bool>.Ok(true);
        }
    }
}

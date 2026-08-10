using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Notifications;

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

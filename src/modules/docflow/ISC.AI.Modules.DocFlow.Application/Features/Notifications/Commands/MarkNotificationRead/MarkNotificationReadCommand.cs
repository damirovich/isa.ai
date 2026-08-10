using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Notifications;

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

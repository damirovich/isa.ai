using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Notifications;

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

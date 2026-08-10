using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Settings;

/// <summary>Запустить проверку сроков вручную, не дожидаясь расписания (§4.2/разд. 5).</summary>
/// <remarks>
/// Нужна в двух случаях: при развёртывании (чтобы не ждать час до первого тика) и при разборе жалобы
/// «почему не пришло уведомление» — иначе проверить гипотезу нечем. Повторный запуск БЕЗОПАСЕН:
/// перевод в «Просрочено» идемпотентен, а уведомления отсекает дедупликация.
/// </remarks>
public sealed record RunDeadlineCheckCommand : IRequest<ResponseDto<DeadlineCheckResult>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => "docflow:deadlines:run-check";

    /// <inheritdoc cref="RunDeadlineCheckCommand" />
    public sealed class Handler(IDeadlineChecker checker, IDocFlowAdministration administration)
        : IRequestHandler<RunDeadlineCheckCommand, ResponseDto<DeadlineCheckResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<DeadlineCheckResult>> Handle(
            RunDeadlineCheckCommand command, CancellationToken cancellationToken)
        {
            // Проверка рассылает уведомления всем участникам и меняет статусы — это не «посмотреть».
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<DeadlineCheckResult>.BadRequest(SettingsGuard.Denied);
            }

            var result = await checker.RunOnceAsync(cancellationToken);
            return ResponseDto<DeadlineCheckResult>.Ok(
                result,
                $"Проверка выполнена: переведено в «Просрочено» — {result.MarkedOverdue}, "
                + $"уведомлений о сроке — {result.DeadlineNotices}.");
        }
    }
}

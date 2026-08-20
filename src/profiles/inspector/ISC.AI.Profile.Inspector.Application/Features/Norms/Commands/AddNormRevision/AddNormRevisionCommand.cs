using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>
/// Добавить редакцию нормы (создаётся «Действующей»). Прежняя редакция автоматически НЕ гасится:
/// «Утратила силу» ей проставляет оператор отдельной командой смены статуса — это осознанное
/// действие с последствиями для выдачи (GATE-3), а не побочный эффект.
/// </summary>
public sealed record AddNormRevisionCommand(int NormId, DateOnly EffectiveDate)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:norm:{NormId}:revision:add:{EffectiveDate:yyyy-MM-dd}";

    /// <inheritdoc cref="AddNormRevisionCommand" />
    public sealed class Handler(INormRegistryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<AddNormRevisionCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            AddNormRevisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await NormGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(NormGuard.Denied);
            }

            var (result, revisionId) = await store.AddRevisionAsync(
                command.NormId, command.EffectiveDate, cancellationToken);
            return result == NormWriteResult.Ok
                ? ResponseDto<int>.Ok(revisionId)
                : ResponseDto<int>.NotFound("Норма не найдена.");
        }
    }
}

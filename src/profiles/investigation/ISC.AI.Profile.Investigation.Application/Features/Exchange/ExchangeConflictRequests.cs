using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Exchange;

/// <summary>
/// Очередь Руководителя «Конфликты обмена» (ТФ-ОБМ-02, ADR-0068 v0.3): дела и носители пакета другого офиса, совпавшие со
/// своими, — в пределах допуска сотрудника (ТБ-020/021). Право — <c>exchange.conflicts</c> (по умолчанию Руководитель).
/// </summary>
/// <param name="IncludeHistory">Показать и применённые/снятые (последние).</param>
public sealed record ListExchangeConflictsQuery(bool IncludeHistory = false) : IRequest<ResponseDto<IReadOnlyList<ExchangeConflictRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:exchange:conflicts" + (IncludeHistory ? ";history" : string.Empty);

    /// <inheritdoc cref="ListExchangeConflictsQuery" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjects, IAccessContextProvider accessProvider, IExchangeConflicts conflicts)
        : IRequestHandler<ListExchangeConflictsQuery, ResponseDto<IReadOnlyList<ExchangeConflictRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ExchangeConflictRow>>> Handle(
            ListExchangeConflictsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.ExchangeConflicts, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<ExchangeConflictRow>>.BadRequest(RoleGuard.Denied(InvestigationPermissions.ExchangeConflicts));
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<ExchangeConflictRow>>.Ok(await conflicts.ListAsync(access, query.IncludeHistory, cancellationToken));
        }
    }
}

/// <summary>
/// Решение Руководителя по конфликту обмена (ТФ-ОБМ-02, ADR-0068 v0.3–v0.4): объединить со своим, создать новое (только дело;
/// при совпадении номера — с новым номером) или пропустить — применяет следующий приём пакета; по конфликту изменений
/// принятой записи — принять версию источника или оставить свою, применяется сразу.
/// </summary>
/// <param name="ConflictId">Конфликт.</param>
/// <param name="Decision">Решение.</param>
/// <param name="NewNumber">Номер нового дела — если совпал номер в подразделении.</param>
/// <param name="Comment">Комментарий к решению (необязателен).</param>
public sealed record DecideExchangeConflictCommand(int ConflictId, ExchangeConflictDecision Decision, string? NewNumber, string? Comment)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>
    /// Номер конфликта, решение и новый номер дела; комментарий — содержимое, в журнал он попадает в защищённой части вместе с
    /// остальным (ТБ-032). Гриф записи журнала — не ниже допуска решающего, а решает он только видимые ему конфликты.
    /// </remarks>
    public string? AuditSummary =>
        "investigation:exchange:conflict:" + ConflictId.ToString(CultureInfo.InvariantCulture) + ";decision=" + Decision
        + (string.IsNullOrWhiteSpace(NewNumber) ? string.Empty : ";number=" + NewNumber.Trim())
        + (string.IsNullOrWhiteSpace(Comment) ? string.Empty : ";comment=" + Comment.Trim());

    /// <inheritdoc cref="DecideExchangeConflictCommand" />
    public sealed class Handler(
        IUserRoleStore roles,
        ISubjectProvider subjects,
        IAccessContextProvider accessProvider,
        IExchangeConflicts conflicts,
        ICaseClosureRegulation closures)
        : IRequestHandler<DecideExchangeConflictCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DecideExchangeConflictCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!await RoleGuard.CallerHasAsync(roles, subjects, InvestigationPermissions.ExchangeConflicts, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.Denied(InvestigationPermissions.ExchangeConflicts));
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await conflicts.DecideAsync(command.ConflictId, command.Decision, command.NewNumber, command.Comment, access, cancellationToken);
            if (!result.Ok)
            {
                return result.NotFound
                    ? ResponseDto<bool>.NotFound(result.Message ?? "Конфликт не найден или недоступен.")
                    : ResponseDto<bool>.Fail(result.Message ?? "Решение не записано.");
            }

            if (!result.AppliedNow)
            {
                return ResponseDto<bool>.Ok(true, "Решение записано — оно применится при повторном приёме пакета.");
            }

            // ИНВАРИАНТ (ТБ-074): принятое закрытие дела другого офиса исполняет тот же регламент удаления шаблонов, что
            // закрытие здесь; сбой — не тишина: решение применено, а оператору сказано, что биометрия осталась.
            if (result.ClosedCaseId is { } caseId)
            {
                var closure = await closures.ExecuteAsync(
                    caseId,
                    "закрытие дела №" + caseId.ToString(CultureInfo.InvariantCulture) + " по решению о конфликте обмена № "
                    + command.ConflictId.ToString(CultureInfo.InvariantCulture),
                    access.NumericSubjectId,
                    cancellationToken);
                if (closure.Outcome == CaseClosureRegulationOutcome.Failed)
                {
                    return ResponseDto<bool>.BadRequest(
                        "Решение применено, дело закрыто, но регламент удаления шаблонов НЕ исполнен (журнал аудита недоступен). "
                        + "Шаблоны лиц дела остались в базе. Закройте дело повторно, когда журнал будет доступен.");
                }
            }

            return ResponseDto<bool>.Ok(true, "Решение применено.");
        }
    }
}

/// <summary>Проверка решения: известное решение, длины номера и комментария.</summary>
public sealed class DecideExchangeConflictValidator : AbstractValidator<DecideExchangeConflictCommand>
{
    /// <summary>Предел комментария.</summary>
    public const int MaxCommentLength = 1000;

    /// <summary>Правила.</summary>
    public DecideExchangeConflictValidator()
    {
        RuleFor(c => c.ConflictId).GreaterThan(0).WithMessage("Не выбран конфликт.");
        RuleFor(c => c.Decision).IsInEnum()
            .WithMessage("Выберите решение: объединить, создать новое, пропустить — или, для изменений, принять версию источника либо оставить свою.");
        RuleFor(c => c.NewNumber).MaximumLength(100).WithMessage("Номер дела длиннее 100 знаков.");
        RuleFor(c => c.Comment).MaximumLength(MaxCommentLength).WithMessage($"Комментарий длиннее {MaxCommentLength} знаков.");
    }
}

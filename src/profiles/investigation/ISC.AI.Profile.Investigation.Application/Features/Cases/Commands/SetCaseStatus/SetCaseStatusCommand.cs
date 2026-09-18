using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Сменить статус дела (ТФ-ДЕЛ-01). Закрытие ставит <c>ClosedAt</c> и ЗАПУСКАЕТ регламент удаления
/// шаблонов лиц дела (ТФ-ДЕЛ-04, ТБ-074) — сам регламент выполняется ОТДЕЛЬНЫМ шагом (задача
/// хранения/пакет «Медиа»), здесь только фиксируется факт закрытия.
/// </summary>
public sealed record SetCaseStatusCommand(int CaseId, CaseStatus Status)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:status:{Status}";

    /// <inheritdoc cref="SetCaseStatusCommand" />
    public sealed class Handler(
        ICaseStore cases, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<SetCaseStatusCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetCaseStatusCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await cases.SetStatusAsync(command.CaseId, command.Status, access, cancellationToken);
            return CaseGuard.ToResponse(result);
        }
    }
}

/// <summary>Правила смены статуса.</summary>
public sealed class SetCaseStatusValidator : AbstractValidator<SetCaseStatusCommand>
{
    /// <summary>Идентификатор положительный; статус — из перечисления.</summary>
    public SetCaseStatusValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.Status).IsInEnum().WithMessage("Неизвестный статус дела.");
    }
}

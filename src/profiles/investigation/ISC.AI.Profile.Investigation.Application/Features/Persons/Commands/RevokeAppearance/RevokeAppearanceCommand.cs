using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Отозвать ошибочное появление фигуранта (ADR-0034): «лицо на материале — не этот человек». Появление остаётся в истории
/// с пометкой «отозвано», причиной, временем и отозвавшим; из пересечений, подсказок эксперту и счётчиков оно уходит.
/// </summary>
/// <param name="AppearanceId">Появление.</param>
/// <param name="Reason">Причина — обязательна: без неё отзыв не проверить.</param>
public sealed record RevokeAppearanceCommand(int AppearanceId, string Reason) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <summary>Отказ: отзывающий сам подтверждал это появление.</summary>
    public const string OwnDecision =
        "Вы подтверждали это появление (эксперт или верификатор) — отозвать его должен другой сотрудник (правило двух лиц).";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Причина — в строке появления под решёткой; в сводку журнала идёт только идентификатор (ТБ-032).</remarks>
    public string? AuditSummary => $"investigation:appearance:{AppearanceId}:revoke";

    /// <inheritdoc cref="RevokeAppearanceCommand" />
    public sealed class Handler(
        IPersonStore persons, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<RevokeAppearanceCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RevokeAppearanceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ТБ-012: право матрицы доступа — до любого чтения.
            if (!await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.VerificationRevoke, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.Denied(InvestigationPermissions.VerificationRevoke));
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return await persons.RevokeAppearanceAsync(command.AppearanceId, command.Reason.Trim(), access, cancellationToken) switch
            {
                AppearanceRevokeResult.Ok => ResponseDto<bool>.Ok(true),
                AppearanceRevokeResult.AlreadyRevoked => ResponseDto<bool>.BadRequest("Появление уже отозвано."),
                AppearanceRevokeResult.OwnDecision => ResponseDto<bool>.BadRequest(OwnDecision),
                _ => ResponseDto<bool>.NotFound("Появление не найдено или недоступно."),
            };
        }
    }
}

/// <summary>Правила отзыва появления.</summary>
public sealed class RevokeAppearanceValidator : AbstractValidator<RevokeAppearanceCommand>
{
    /// <summary>Наибольшая длина причины.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>Появление указано; причина — осмысленная (не короче 10 знаков) и не длиннее предела.</summary>
    public RevokeAppearanceValidator()
    {
        RuleFor(c => c.AppearanceId).GreaterThan(0);
        RuleFor(c => c.Reason)
            .Must(r => !string.IsNullOrWhiteSpace(r) && r.Trim().Length >= 10)
            .WithMessage("Укажите причину отзыва — не короче 10 знаков.")
            .MaximumLength(MaxReasonLength);
    }
}

/// <summary>Может ли текущий сотрудник отзывать появления — чтобы карточка показала кнопку или объяснила, почему её нет.</summary>
/// <param name="CanRevoke">Право «Отзыв ошибочного появления» открыто.</param>
/// <param name="UserId">Текущий пользователь: свои подтверждения он отозвать не может.</param>
public sealed record AppearanceRevokeRight(bool CanRevoke, int? UserId);

/// <summary>Право текущего сотрудника на отзыв появлений (для подсказок интерфейса; сервер проверяет право в команде).</summary>
public sealed record GetAppearanceRevokeRightQuery : IRequest<ResponseDto<AppearanceRevokeRight>>
{
    /// <inheritdoc cref="GetAppearanceRevokeRightQuery" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<GetAppearanceRevokeRightQuery, ResponseDto<AppearanceRevokeRight>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AppearanceRevokeRight>> Handle(
            GetAppearanceRevokeRightQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var can = await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.VerificationRevoke, cancellationToken);
            return ResponseDto<AppearanceRevokeRight>.Ok(
                new AppearanceRevokeRight(can, await subjectProvider.GetCurrentUserIdAsync(cancellationToken)));
        }
    }
}

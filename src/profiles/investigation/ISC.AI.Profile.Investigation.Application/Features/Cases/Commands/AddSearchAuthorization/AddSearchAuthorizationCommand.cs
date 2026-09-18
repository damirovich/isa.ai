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
/// Внести основание поиска по лицу в дело (ТБ-071): поручение следователя, постановление или номер ОРМ.
/// Без основания поиск технически невозможен; реквизиты основания попадают в аудит каждого поиска (ТБ-072).
/// </summary>
public sealed record AddSearchAuthorizationCommand(
    int CaseId,
    AuthorizationKind Kind,
    string Reference,
    DateOnly IssuedAt,
    DateOnly? ValidUntil = null,
    string? Notes = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:authorization:add:kind={Kind};ref={Reference}";

    /// <inheritdoc cref="AddSearchAuthorizationCommand" />
    public sealed class Handler(
        ICaseStore cases, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<AddSearchAuthorizationCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            AddSearchAuthorizationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var draft = new SearchAuthorizationDraft(
                command.CaseId, command.Kind, command.Reference.Trim(), command.IssuedAt,
                access.NumericSubjectId, command.ValidUntil,
                string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim());

            var (result, authorizationId) = await cases.AddAuthorizationAsync(draft, access, cancellationToken);
            return result switch
            {
                CaseWriteResult.Ok => ResponseDto<int>.Ok(authorizationId),
                CaseWriteResult.OutsideClearance => ResponseDto<int>.BadRequest(CaseGuard.OutsideClearance),
                _ => ResponseDto<int>.NotFound(CaseGuard.NotFound),
            };
        }
    }
}

/// <summary>Правила формы основания поиска (ТБ-071).</summary>
public sealed class AddSearchAuthorizationValidator : AbstractValidator<AddSearchAuthorizationCommand>
{
    /// <summary>Дело обязательно; вид из перечисления; реквизиты ≤300 непустые; срок действия не раньше выдачи.</summary>
    public AddSearchAuthorizationValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.Kind).IsInEnum().WithMessage("Неизвестный вид основания.");
        RuleFor(c => c.Reference).NotEmpty().WithMessage("Укажите реквизиты основания.").MaximumLength(300);
        RuleFor(c => c.ValidUntil)
            .GreaterThanOrEqualTo(c => c.IssuedAt)
            .When(c => c.ValidUntil is not null)
            .WithMessage("Срок действия не может быть раньше даты выдачи.");
        RuleFor(c => c.Notes).MaximumLength(2000);
    }
}

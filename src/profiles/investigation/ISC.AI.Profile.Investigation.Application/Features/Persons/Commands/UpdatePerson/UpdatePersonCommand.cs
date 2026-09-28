using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Изменить реквизиты, роль и анкету фигуранта (ТФ-ПЕР-01/05) — заменяются целиком; дело, гриф и
/// подразделение не меняются. Роль и анкета — обязательные параметры без умолчаний: правка заменяет их
/// целиком, и вызов, «забывший» их передать, молча стёр бы анкету. <see langword="null"/> в
/// <paramref name="Questionnaire"/> — осознанная очистка анкеты.
/// </summary>
public sealed record UpdatePersonCommand(
    int PersonId,
    string? DisplayName,
    bool IsUnidentified,
    PersonRole Role,
    PersonQuestionnaire? Questionnaire,
    string? RoleInCase = null,
    string? Notes = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Анкета — персональные данные: в сводку идут только признак и роль по перечню (ТБ-032).</remarks>
    public string? AuditSummary => $"investigation:person:{PersonId}:update:unidentified={IsUnidentified};role={Role}";

    /// <inheritdoc cref="UpdatePersonCommand" />
    public sealed class Handler(
        IPersonStore persons, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<UpdatePersonCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(UpdatePersonCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // CaseId черновика правки хранилище игнорирует (дело фигуранта не меняется) — передаём 0.
            var edit = new PersonDraft(
                CaseId: 0,
                string.IsNullOrWhiteSpace(command.DisplayName) ? null : command.DisplayName.Trim(),
                command.IsUnidentified,
                string.IsNullOrWhiteSpace(command.RoleInCase) ? null : command.RoleInCase.Trim(),
                string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
                command.Role,
                command.Questionnaire);
            var result = await persons.UpdateAsync(command.PersonId, edit, access, cancellationToken);
            return PersonGuard.ToResponse(result);
        }
    }
}

/// <inheritdoc cref="CreatePersonValidator" />
public sealed class UpdatePersonValidator : AbstractValidator<UpdatePersonCommand>
{
    /// <summary>Идентификатор положительный; остальное — как при создании.</summary>
    public UpdatePersonValidator()
    {
        RuleFor(c => c.PersonId).GreaterThan(0);
        RuleFor(c => c.DisplayName)
            .NotEmpty().When(c => !c.IsUnidentified)
            .WithMessage("Укажите установочные данные либо отметьте «личность не установлена».");
        RuleFor(c => c.DisplayName).MaximumLength(500);
        RuleFor(c => c.Role).IsInEnum().WithMessage("Неизвестная роль фигуранта.");
        RuleFor(c => c.RoleInCase).MaximumLength(200);
        RuleFor(c => c.Notes).MaximumLength(4000);
        RuleFor(c => c.Questionnaire!).SetValidator(new PersonQuestionnaireValidator()).When(c => c.Questionnaire is not null);
    }
}

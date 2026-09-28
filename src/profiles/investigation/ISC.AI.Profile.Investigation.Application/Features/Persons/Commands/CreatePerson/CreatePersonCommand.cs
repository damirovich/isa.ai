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
/// Завести фигуранта в деле (ТФ-ПЕР-01). Пустое имя при <paramref name="IsUnidentified"/> хранилище заменяет
/// на «Неустановленное лицо № N». Гриф и подразделение фигуранта — дела (ТБ-070), выбора нет.
/// Роль — по перечню (объект, связь, иная); анкета (ТФ-ПЕР-05) необязательна; у роли «связь» — чья это связь
/// и кем приходится (ТФ-ПЕР-06).
/// </summary>
public sealed record CreatePersonCommand(
    int CaseId,
    string? DisplayName,
    bool IsUnidentified,
    string? RoleInCase = null,
    string? Notes = null,
    PersonRole Role = PersonRole.Other,
    PersonQuestionnaire? Questionnaire = null,
    int? LinkedToPersonId = null,
    int? LinkTypeId = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>
    /// Установочные данные и анкета — персональные данные: в сводку идут только дело, признак и роль по
    /// перечню (ТБ-032).
    /// </remarks>
    public string? AuditSummary => $"investigation:case:{CaseId}:person:create:unidentified={IsUnidentified};role={Role}";

    /// <inheritdoc cref="CreatePersonCommand" />
    public sealed class Handler(
        IPersonStore persons, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<CreatePersonCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(CreatePersonCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            // Fail-closed (ТБ-021): дело должно быть доступно субъекту — проверяет хранилище под решёткой.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var draft = new PersonDraft(
                command.CaseId,
                string.IsNullOrWhiteSpace(command.DisplayName) ? null : command.DisplayName.Trim(),
                command.IsUnidentified,
                string.IsNullOrWhiteSpace(command.RoleInCase) ? null : command.RoleInCase.Trim(),
                string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
                command.Role,
                command.Questionnaire,
                command.LinkedToPersonId,
                command.LinkTypeId);

            var (result, personId) = await persons.CreateAsync(draft, access, cancellationToken);
            return result switch
            {
                PersonWriteResult.Ok => ResponseDto<int>.Ok(personId),
                PersonWriteResult.InvalidLink => ResponseDto<int>.BadRequest(PersonGuard.InvalidLink),
                _ => ResponseDto<int>.NotFound(PersonGuard.NotFound),
            };
        }
    }
}

/// <summary>Правила формы фигуранта: у установленного лица имя обязательно.</summary>
public sealed class CreatePersonValidator : AbstractValidator<CreatePersonCommand>
{
    /// <summary>
    /// Дело обязательно; имя ≤500 (обязательно, если личность установлена); роль из перечня, уточнение ≤200;
    /// примечания ≤4000; анкета — по <see cref="PersonQuestionnaireValidator"/>.
    /// </summary>
    public CreatePersonValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.DisplayName)
            .NotEmpty().When(c => !c.IsUnidentified)
            .WithMessage("Укажите установочные данные либо отметьте «личность не установлена».");
        RuleFor(c => c.DisplayName).MaximumLength(500);
        RuleFor(c => c.Role).IsInEnum().WithMessage("Неизвестная роль фигуранта.");
        RuleFor(c => c.RoleInCase).MaximumLength(200);
        RuleFor(c => c.Notes).MaximumLength(4000);
        RuleFor(c => c.Questionnaire!).SetValidator(new PersonQuestionnaireValidator()).When(c => c.Questionnaire is not null);

        // ТФ-ПЕР-06: поля связи — только у роли «связь».
        RuleFor(c => c.LinkedToPersonId).Null().When(c => c.Role != PersonRole.Link).WithMessage(PersonLinkRules.OnlyForLink);
        RuleFor(c => c.LinkTypeId).Null().When(c => c.Role != PersonRole.Link).WithMessage(PersonLinkRules.OnlyForLink);
        RuleFor(c => c.LinkedToPersonId).GreaterThan(0).When(c => c.LinkedToPersonId is not null);
        RuleFor(c => c.LinkTypeId).GreaterThan(0).When(c => c.LinkTypeId is not null);
    }
}

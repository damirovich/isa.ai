using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Завести фигуранта в деле (ТФ-ПЕР-01). Пустое имя при <paramref name="IsUnidentified"/> хранилище заменяет
/// на «Неустановленное лицо № N». Гриф и подразделение фигуранта — дела (ТБ-070), выбора нет.
/// </summary>
public sealed record CreatePersonCommand(
    int CaseId,
    string? DisplayName,
    bool IsUnidentified,
    string? RoleInCase = null,
    string? Notes = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Установочные данные — персональные данные: в сводку идёт только дело и признак (ТБ-032).</remarks>
    public string? AuditSummary => $"investigation:case:{CaseId}:person:create:unidentified={IsUnidentified}";

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
                string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim());

            var (result, personId) = await persons.CreateAsync(draft, access, cancellationToken);
            return result == PersonWriteResult.Ok
                ? ResponseDto<int>.Ok(personId)
                : ResponseDto<int>.NotFound(PersonGuard.NotFound);
        }
    }
}

/// <summary>Правила формы фигуранта: у установленного лица имя обязательно.</summary>
public sealed class CreatePersonValidator : AbstractValidator<CreatePersonCommand>
{
    /// <summary>Дело обязательно; имя ≤500 (обязательно, если личность установлена); роль ≤200; примечания ≤4000.</summary>
    public CreatePersonValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.DisplayName)
            .NotEmpty().When(c => !c.IsUnidentified)
            .WithMessage("Укажите установочные данные либо отметьте «личность не установлена».");
        RuleFor(c => c.DisplayName).MaximumLength(500);
        RuleFor(c => c.RoleInCase).MaximumLength(200);
        RuleFor(c => c.Notes).MaximumLength(4000);
    }
}

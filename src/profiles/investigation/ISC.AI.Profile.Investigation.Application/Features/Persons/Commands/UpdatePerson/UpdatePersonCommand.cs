using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Изменить реквизиты фигуранта (ТФ-ПЕР-01); дело, гриф и подразделение не меняются.</summary>
public sealed record UpdatePersonCommand(
    int PersonId,
    string? DisplayName,
    bool IsUnidentified,
    string? RoleInCase = null,
    string? Notes = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:{PersonId}:update:unidentified={IsUnidentified}";

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
            var result = await persons.UpdateAsync(
                command.PersonId,
                string.IsNullOrWhiteSpace(command.DisplayName) ? null : command.DisplayName.Trim(),
                command.IsUnidentified,
                string.IsNullOrWhiteSpace(command.RoleInCase) ? null : command.RoleInCase.Trim(),
                string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim(),
                access, cancellationToken);
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
        RuleFor(c => c.RoleInCase).MaximumLength(200);
        RuleFor(c => c.Notes).MaximumLength(4000);
    }
}

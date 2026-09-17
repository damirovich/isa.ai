using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Divisions;

/// <summary>Создать подразделение (корневое либо дочернее к <paramref name="ParentId"/>) (ТФ-АДМ-01).</summary>
public sealed record CreateDivisionCommand(string Name, string? Code = null, int? ParentId = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:division:create:{Name}";

    /// <inheritdoc cref="CreateDivisionCommand" />
    public sealed class Handler(IDivisionAdminStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<CreateDivisionCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(CreateDivisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Справочник — словарь решётки доступа (ТБ-020): правит только Администратор (ТП-004).
            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.AdminDenied);
            }

            var id = await store.CreateAsync(
                command.Name.Trim(),
                string.IsNullOrWhiteSpace(command.Code) ? null : command.Code.Trim(),
                command.ParentId, cancellationToken);
            return ResponseDto<int>.Ok(id);
        }
    }
}

/// <summary>Правила формы подразделения: имя обязательно ≤500; код ≤100; родитель положительный.</summary>
public sealed class CreateDivisionValidator : AbstractValidator<CreateDivisionCommand>
{
    /// <inheritdoc cref="CreateDivisionValidator" />
    public CreateDivisionValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование подразделения.").MaximumLength(500);
        RuleFor(c => c.Code).MaximumLength(100);
        RuleFor(c => c.ParentId).GreaterThan(0).When(c => c.ParentId is not null);
    }
}

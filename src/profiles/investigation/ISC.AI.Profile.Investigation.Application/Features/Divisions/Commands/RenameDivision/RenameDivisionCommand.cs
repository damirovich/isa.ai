using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Divisions;

/// <summary>Переименовать подразделение / изменить код (ТФ-АДМ-01).</summary>
public sealed record RenameDivisionCommand(int Id, string Name, string? Code = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:division:{Id}:rename";

    /// <inheritdoc cref="RenameDivisionCommand" />
    public sealed class Handler(IDivisionAdminStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<RenameDivisionCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RenameDivisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.AdminDenied);
            }

            var result = await store.RenameAsync(
                command.Id, command.Name.Trim(),
                string.IsNullOrWhiteSpace(command.Code) ? null : command.Code.Trim(),
                cancellationToken);
            return result == DivisionWriteResult.Ok
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Подразделение не найдено.");
        }
    }
}

/// <inheritdoc cref="CreateDivisionValidator" />
public sealed class RenameDivisionValidator : AbstractValidator<RenameDivisionCommand>
{
    /// <summary>Идентификатор положительный; имя обязательно ≤500; код ≤100.</summary>
    public RenameDivisionValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование подразделения.").MaximumLength(500);
        RuleFor(c => c.Code).MaximumLength(100);
    }
}

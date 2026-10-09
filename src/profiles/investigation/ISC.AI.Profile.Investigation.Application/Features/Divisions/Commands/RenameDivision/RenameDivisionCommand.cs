using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Divisions;

/// <summary>Переименовать подразделение, изменить код и отметку отдела ОН/ОУ (ТФ-АДМ-01, ТЭ-008).</summary>
/// <remarks>
/// <paramref name="Direction"/> — отметка отдела (ADR-0039); <see langword="null"/> — своей нет, отдел берётся у
/// вышестоящего. Смена отметки переносит в другой отдел дела подразделения и вложенных без своей отметки — поэтому
/// новое значение пишется в журнал (ТБ-030).
/// </remarks>
public sealed record RenameDivisionCommand(int Id, string Name, string? Code = null, CaseDirection? Direction = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:division:{Id}:rename:direction={Direction?.ToString() ?? "-"}";

    /// <inheritdoc cref="RenameDivisionCommand" />
    public sealed class Handler(IDivisionAdminStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<RenameDivisionCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RenameDivisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanManageDirectoriesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.DirectoriesDenied);
            }

            var result = await store.RenameAsync(
                command.Id, command.Name.Trim(),
                string.IsNullOrWhiteSpace(command.Code) ? null : command.Code.Trim(),
                command.Direction, cancellationToken);
            return result == DivisionWriteResult.Ok
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Подразделение не найдено.");
        }
    }
}

/// <inheritdoc cref="CreateDivisionValidator" />
public sealed class RenameDivisionValidator : AbstractValidator<RenameDivisionCommand>
{
    /// <summary>Идентификатор положительный; имя обязательно ≤500; код ≤100; отдел — ОН или ОУ.</summary>
    public RenameDivisionValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование подразделения.").MaximumLength(500);
        RuleFor(c => c.Code).MaximumLength(100);
        RuleFor(c => c.Direction).IsInEnum().WithMessage(CreateDivisionValidator.DirectionInvalid);
    }
}

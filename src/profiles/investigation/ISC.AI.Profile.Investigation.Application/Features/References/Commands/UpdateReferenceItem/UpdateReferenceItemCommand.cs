using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.References;

/// <summary>
/// Изменить наименование, код и порядок записи справочника (ТФ-АДМ-07). Вид записи не меняется;
/// переименование видно во всех делах, где запись стоит, — для смены смысла заводится новая запись.
/// </summary>
/// <param name="Id">Запись.</param>
/// <param name="Name">Наименование.</param>
/// <param name="Code">Код.</param>
/// <param name="SortOrder">Порядок в списках.</param>
public sealed record UpdateReferenceItemCommand(int Id, string Name, string? Code = null, int SortOrder = 0)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:reference:{Id}:update:{ReferenceGuard.AuditName(Name)}";

    /// <inheritdoc cref="UpdateReferenceItemCommand" />
    public sealed class Handler(IReferenceStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<UpdateReferenceItemCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(UpdateReferenceItemCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanManageDirectoriesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.DirectoriesDenied);
            }

            var result = await store.UpdateAsync(
                command.Id, command.Name.Trim(),
                string.IsNullOrWhiteSpace(command.Code) ? null : command.Code.Trim(),
                command.SortOrder, cancellationToken);
            return ReferenceGuard.ToResponse(result);
        }
    }
}

/// <inheritdoc cref="CreateReferenceItemValidator" />
public sealed class UpdateReferenceItemValidator : AbstractValidator<UpdateReferenceItemCommand>
{
    /// <summary>Идентификатор положительный; остальное — как при создании.</summary>
    public UpdateReferenceItemValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование.").MaximumLength(ReferenceGuard.MaxNameLength);
        RuleFor(c => c.Code).MaximumLength(ReferenceGuard.MaxCodeLength);
    }
}

using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.References;

/// <summary>Добавить запись в справочник профиля (ТФ-АДМ-07). Ведёт Администратор, изменение — в журнале аудита.</summary>
/// <param name="Kind">Вид справочника.</param>
/// <param name="Name">Наименование.</param>
/// <param name="Code">Код.</param>
/// <param name="SortOrder">Порядок в списках (звания — по старшинству).</param>
public sealed record CreateReferenceItemCommand(ReferenceKind Kind, string Name, string? Code = null, int SortOrder = 0)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Наименование справочника режимных сведений не содержит (как у подразделений) — пишется в сводку.</remarks>
    public string? AuditSummary => $"investigation:reference:create:kind={Kind}:{ReferenceGuard.AuditName(Name)}";

    /// <inheritdoc cref="CreateReferenceItemCommand" />
    public sealed class Handler(IReferenceStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<CreateReferenceItemCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(CreateReferenceItemCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Справочники — словарь заданий всех дел: правит только Администратор (ТФ-АДМ-07, ТП-004).
            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.AdminDenied);
            }

            var (result, id) = await store.CreateAsync(
                command.Kind, command.Name.Trim(),
                string.IsNullOrWhiteSpace(command.Code) ? null : command.Code.Trim(),
                command.SortOrder, cancellationToken);
            return result == ReferenceWriteResult.Ok
                ? ResponseDto<int>.Ok(id)
                : ResponseDto<int>.Conflict(ReferenceGuard.Duplicate);
        }
    }
}

/// <summary>Правила формы записи справочника: вид из перечня, наименование обязательно, длины в пределах таблицы.</summary>
public sealed class CreateReferenceItemValidator : AbstractValidator<CreateReferenceItemCommand>
{
    /// <inheritdoc cref="CreateReferenceItemValidator" />
    public CreateReferenceItemValidator()
    {
        RuleFor(c => c.Kind).IsInEnum().WithMessage("Неизвестный справочник.");
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование.").MaximumLength(ReferenceGuard.MaxNameLength);
        RuleFor(c => c.Code).MaximumLength(ReferenceGuard.MaxCodeLength);
    }
}

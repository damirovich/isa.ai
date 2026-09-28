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
/// Добавить (<paramref name="AddressId"/> = <see langword="null"/>) или изменить адрес фигуранта (ТФ-ПЕР-06).
/// Ведут Следователь, Руководитель, Администратор (ТП-004); гриф адреса — фигуранта (ТБ-070).
/// </summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="AddressId">Изменяемый адрес; <see langword="null"/> — новый.</param>
/// <param name="Kind">Вид адреса.</param>
/// <param name="Text">Адрес.</param>
/// <param name="Notes">Примечание.</param>
public sealed record SavePersonAddressCommand(int PersonId, int? AddressId, AddressKind Kind, string Text, string? Notes = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Адрес — персональные данные: в сводку идут только фигурант, вид и номер записи (ТБ-032).</remarks>
    public string? AuditSummary =>
        $"investigation:person:{PersonId}:address:{(AddressId is { } id ? $"update:{id}" : "add")}:kind={Kind}";

    /// <inheritdoc cref="SavePersonAddressCommand" />
    public sealed class Handler(
        IPersonRequisiteStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<SavePersonAddressCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(SavePersonAddressCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var (result, id) = await store.SaveAddressAsync(
                command.PersonId, command.AddressId,
                new PersonAddressDraft(command.Kind, command.Text.Trim(), string.IsNullOrWhiteSpace(command.Notes) ? null : command.Notes.Trim()),
                access, cancellationToken);
            return result == PersonWriteResult.Ok
                ? ResponseDto<int>.Ok(id)
                : ResponseDto<int>.NotFound(PersonGuard.NotFound);
        }
    }
}

/// <summary>Правила формы адреса: вид из перечня, адрес с буквами или цифрами, длины в пределах таблицы.</summary>
public sealed class SavePersonAddressValidator : AbstractValidator<SavePersonAddressCommand>
{
    /// <summary>Предел длины адреса.</summary>
    public const int MaxTextLength = 1000;

    /// <summary>Предел длины примечания.</summary>
    public const int MaxNotesLength = 2000;

    /// <inheritdoc cref="SavePersonAddressValidator" />
    public SavePersonAddressValidator()
    {
        RuleFor(c => c.PersonId).GreaterThan(0);
        RuleFor(c => c.AddressId).GreaterThan(0).When(c => c.AddressId is not null);
        RuleFor(c => c.Kind).IsInEnum().WithMessage("Неизвестный вид адреса.");
        RuleFor(c => c.Text)
            .NotEmpty().WithMessage("Укажите адрес.")
            .Must(text => text is not null && text.Any(char.IsLetterOrDigit)).WithMessage("Адрес должен содержать буквы или цифры.")
            .MaximumLength(MaxTextLength);
        RuleFor(c => c.Notes).MaximumLength(MaxNotesLength);
    }
}

/// <summary>Удалить адрес фигуранта (ошибка ввода, ТФ-ПЕР-06); удаление — в журнале аудита.</summary>
/// <param name="AddressId">Адрес.</param>
public sealed record DeletePersonAddressCommand(int AddressId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:address:{AddressId}:delete";

    /// <inheritdoc cref="DeletePersonAddressCommand" />
    public sealed class Handler(
        IPersonRequisiteStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<DeletePersonAddressCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeletePersonAddressCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return PersonGuard.ToResponse(await store.DeleteAddressAsync(command.AddressId, access, cancellationToken));
        }
    }
}

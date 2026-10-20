using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
/// Добавить (<paramref name="PhoneId"/> = <see langword="null"/>) или изменить номер телефона фигуранта или связи
/// (ТФ-ПЕР-12, ADR-0055). Номер хранится как введён и нормализованным (только цифры, местный — с 996) — по нему
/// ищутся пересечения и номер в едином поиске. Ведут Следователь, Руководитель, Администратор (ТП-004).
/// </summary>
/// <param name="PersonId">Фигурант (объект или связь).</param>
/// <param name="PhoneId">Изменяемая запись; <see langword="null"/> — новая.</param>
/// <param name="Kind">Вид номера.</param>
/// <param name="Number">Номер как введён.</param>
/// <param name="Notes">Примечание.</param>
public sealed record SavePersonPhoneCommand(
    int PersonId,
    int? PhoneId,
    PhoneKind Kind,
    string Number,
    string? Notes = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Номер телефона — режимный реквизит работы за объектом: в сводку он не идёт (ТБ-032).</remarks>
    public string? AuditSummary =>
        $"investigation:person:{PersonId}:phone:{(PhoneId is { } id ? $"update:{id}" : "add")}";

    /// <inheritdoc cref="SavePersonPhoneCommand" />
    public sealed class Handler(
        IPersonRequisiteStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<SavePersonPhoneCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(SavePersonPhoneCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            // Валидатор конвейера уже не пропустил номер без цифр; повтор здесь — для прямого вызова обработчика.
            if (RequisiteNormalizer.Phone(command.Number) is null)
            {
                return ResponseDto<int>.BadRequest(SavePersonPhoneValidator.DigitsRequiredMessage);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var draft = new PersonPhoneDraft(command.Kind, command.Number.Trim(), Clean(command.Notes));
            var (result, id) = await store.SavePhoneAsync(command.PersonId, command.PhoneId, draft, access, cancellationToken);
            return result == PersonWriteResult.Ok
                ? ResponseDto<int>.Ok(id)
                : ResponseDto<int>.NotFound(PersonGuard.NotFound);
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>Правила формы номера: вид из перечня, номер с цифрами и в пределах длины таблицы.</summary>
public sealed class SavePersonPhoneValidator : AbstractValidator<SavePersonPhoneCommand>
{
    /// <summary>Предел длины номера как введён (с пробелами, скобками и «+»).</summary>
    public const int MaxNumberLength = 50;

    /// <summary>Наибольшее число цифр номера (столбец нормализованного номера).</summary>
    public const int MaxDigits = 32;

    /// <summary>Предел длины примечания.</summary>
    public const int MaxNotesLength = 500;

    /// <summary>Текст отказа: в номере нет цифр.</summary>
    public const string DigitsRequiredMessage = "Номер телефона должен содержать цифры.";

    /// <inheritdoc cref="SavePersonPhoneValidator" />
    public SavePersonPhoneValidator()
    {
        RuleFor(c => c.PersonId).GreaterThan(0);
        RuleFor(c => c.PhoneId).GreaterThan(0).When(c => c.PhoneId is not null);
        RuleFor(c => c.Kind).IsInEnum().WithMessage("Неизвестный вид номера.");
        RuleFor(c => c.Number)
            .NotEmpty().WithMessage("Укажите номер телефона.")
            .MaximumLength(MaxNumberLength)
            .Must(number => RequisiteNormalizer.Phone(number) is not null).WithMessage(DigitsRequiredMessage)
            .Must(number => number is null || number.Count(char.IsAsciiDigit) <= MaxDigits)
            .WithMessage($"В номере не больше {MaxDigits} цифр.");
        RuleFor(c => c.Notes).MaximumLength(MaxNotesLength);
    }
}

/// <summary>Удалить номер телефона фигуранта (ошибка ввода, ТФ-ПЕР-12); удаление — в журнале аудита.</summary>
/// <param name="PhoneId">Запись номера.</param>
public sealed record DeletePersonPhoneCommand(int PhoneId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:phone:{PhoneId}:delete";

    /// <inheritdoc cref="DeletePersonPhoneCommand" />
    public sealed class Handler(
        IPersonRequisiteStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<DeletePersonPhoneCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeletePersonPhoneCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return PersonGuard.ToResponse(await store.DeletePhoneAsync(command.PhoneId, access, cancellationToken));
        }
    }
}

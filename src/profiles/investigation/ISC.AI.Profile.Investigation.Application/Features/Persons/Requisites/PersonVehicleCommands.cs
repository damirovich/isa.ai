using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Добавить (<paramref name="VehicleId"/> = <see langword="null"/>) или изменить автотранспорт фигуранта
/// (ТФ-ПЕР-06). Нужен госномер или хотя бы марка. Ведут Следователь, Руководитель, Администратор (ТП-004).
/// </summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="VehicleId">Изменяемая запись; <see langword="null"/> — новая.</param>
/// <param name="PlateNumber">Госномер.</param>
/// <param name="Make">Марка.</param>
/// <param name="Model">Модель.</param>
/// <param name="Color">Цвет.</param>
/// <param name="Notes">Примечание.</param>
public sealed record SavePersonVehicleCommand(
    int PersonId,
    int? VehicleId,
    string? PlateNumber,
    string? Make = null,
    string? Model = null,
    string? Color = null,
    string? Notes = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Госномер — режимный реквизит работы за объектом: в сводку он не идёт (ТБ-032).</remarks>
    public string? AuditSummary =>
        $"investigation:person:{PersonId}:vehicle:{(VehicleId is { } id ? $"update:{id}" : "add")}";

    /// <inheritdoc cref="SavePersonVehicleCommand" />
    public sealed class Handler(
        IPersonRequisiteStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<SavePersonVehicleCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(SavePersonVehicleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var draft = new PersonVehicleDraft(
                Clean(command.PlateNumber), Clean(command.Make), Clean(command.Model), Clean(command.Color), Clean(command.Notes));
            var (result, id) = await store.SaveVehicleAsync(command.PersonId, command.VehicleId, draft, access, cancellationToken);
            return result == PersonWriteResult.Ok
                ? ResponseDto<int>.Ok(id)
                : ResponseDto<int>.NotFound(PersonGuard.NotFound);
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>Правила формы автотранспорта: госномер или марка обязательны, длины в пределах таблицы.</summary>
public sealed class SavePersonVehicleValidator : AbstractValidator<SavePersonVehicleCommand>
{
    /// <summary>Предел длины госномера.</summary>
    public const int MaxPlateLength = 20;

    /// <summary>Предел длины марки и модели.</summary>
    public const int MaxMakeLength = 100;

    /// <summary>Предел длины цвета.</summary>
    public const int MaxColorLength = 50;

    /// <summary>Предел длины примечания.</summary>
    public const int MaxNotesLength = 2000;

    /// <inheritdoc cref="SavePersonVehicleValidator" />
    public SavePersonVehicleValidator()
    {
        RuleFor(c => c.PersonId).GreaterThan(0);
        RuleFor(c => c.VehicleId).GreaterThan(0).When(c => c.VehicleId is not null);
        RuleFor(c => c)
            .Must(c => !string.IsNullOrWhiteSpace(c.PlateNumber) || !string.IsNullOrWhiteSpace(c.Make))
            .WithMessage("Укажите госномер или хотя бы марку.");
        RuleFor(c => c.PlateNumber)
            .Must(plate => plate is null || plate.Trim().Length == 0 || plate.Any(char.IsLetterOrDigit))
            .WithMessage("Госномер должен содержать буквы или цифры.")
            .MaximumLength(MaxPlateLength);
        RuleFor(c => c.Make).MaximumLength(MaxMakeLength);
        RuleFor(c => c.Model).MaximumLength(MaxMakeLength);
        RuleFor(c => c.Color).MaximumLength(MaxColorLength);
        RuleFor(c => c.Notes).MaximumLength(MaxNotesLength);
    }
}

/// <summary>Удалить автотранспорт фигуранта (ошибка ввода, ТФ-ПЕР-06); удаление — в журнале аудита.</summary>
/// <param name="VehicleId">Запись автотранспорта.</param>
public sealed record DeletePersonVehicleCommand(int VehicleId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:person:vehicle:{VehicleId}:delete";

    /// <inheritdoc cref="DeletePersonVehicleCommand" />
    public sealed class Handler(
        IPersonRequisiteStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<DeletePersonVehicleCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeletePersonVehicleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return PersonGuard.ToResponse(await store.DeleteVehicleAsync(command.VehicleId, access, cancellationToken));
        }
    }
}

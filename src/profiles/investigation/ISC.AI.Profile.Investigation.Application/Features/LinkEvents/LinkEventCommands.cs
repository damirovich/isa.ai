using System;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.LinkEvents;

/// <summary>Общие тексты и разбор исходов записи событий (ТФ-ПЕР-11, ADR-0056).</summary>
public static class LinkEventGuard
{
    /// <summary>Неразличимый ответ «нет такого объекта, события или участия либо недоступны» (ТБ-020/021).</summary>
    public const string NotFound = "Объект, событие или участие не найдены либо недоступны.";

    /// <summary>Отказ самосвязи — текст [АИС табл. 7.1 № 3].</summary>
    public const string SelfLink = "Ошибка топологии: объект не может иметь ребро связи на самого себя.";

    /// <summary>Оба объекта уже участвуют в выбранном событии.</summary>
    public const string AlreadyLinked = "Объекты уже связаны через это событие.";

    /// <summary>Перевод исхода записи в конверт ответа для команд без полезной нагрузки.</summary>
    public static ResponseDto<bool> ToResponse(LinkEventWriteResult result) => result switch
    {
        LinkEventWriteResult.Ok => ResponseDto<bool>.Ok(true),
        LinkEventWriteResult.SelfLink => ResponseDto<bool>.BadRequest(SelfLink),
        LinkEventWriteResult.AlreadyLinked => ResponseDto<bool>.BadRequest(AlreadyLinked),
        _ => ResponseDto<bool>.NotFound(NotFound),
    };
}

/// <summary>
/// Связать два объекта через событие — «дело-посредник» (ТФ-ПЕР-11, ADR-0056 п. 2): существующее
/// (<paramref name="EventId"/>) или новое (<paramref name="NewEvent"/>). Ведут роли, ведущие дела (ТП-004); связь заводит
/// только человек (ТБ-073). Ответ — номер события.
/// </summary>
/// <param name="PersonId">Объект, из карточки которого заводится связь.</param>
/// <param name="OtherPersonId">Второй объект (фигурант любого доступного субъекту дела).</param>
/// <param name="EventId">Существующее событие.</param>
/// <param name="NewEvent">Новое событие.</param>
/// <param name="PersonRole">Роль первого объекта в событии.</param>
/// <param name="OtherPersonRole">Роль второго объекта в событии.</param>
/// <param name="Contact">Уровень связи, отметка «в общем поиске», транспорт и адрес контакта новых участников.</param>
public sealed record LinkPersonsViaEventCommand(
    int PersonId,
    int OtherPersonId,
    int? EventId,
    LinkEventDraft? NewEvent,
    string? PersonRole,
    string? OtherPersonRole,
    LinkParticipantDraft Contact)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Название, описание, адрес и транспорт — сведения об объектах: в сводку не идут (ТБ-032).</remarks>
    public string? AuditSummary =>
        $"investigation:event:{(EventId is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "new")}:link:person:{PersonId}:person:{OtherPersonId}";

    /// <inheritdoc cref="LinkPersonsViaEventCommand" />
    public sealed class Handler(
        ILinkEventStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<LinkPersonsViaEventCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(LinkPersonsViaEventCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var userId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            var draft = new LinkPersonsDraft(
                command.PersonId, command.OtherPersonId, command.EventId, command.NewEvent,
                command.PersonRole, command.OtherPersonRole, command.Contact);
            var (result, eventId) = await store.LinkAsync(draft, access, userId, cancellationToken);
            return result switch
            {
                LinkEventWriteResult.Ok => ResponseDto<int>.Ok(eventId),
                LinkEventWriteResult.SelfLink => ResponseDto<int>.BadRequest(LinkEventGuard.SelfLink),
                LinkEventWriteResult.AlreadyLinked => ResponseDto<int>.BadRequest(LinkEventGuard.AlreadyLinked),
                _ => ResponseDto<int>.NotFound(LinkEventGuard.NotFound),
            };
        }
    }
}

/// <summary>Правила связи через событие: два разных объекта, ровно одно событие (существующее или новое), поля в пределах.</summary>
public sealed class LinkPersonsViaEventValidator : AbstractValidator<LinkPersonsViaEventCommand>
{
    /// <summary>Текст отказа: не выбрано событие или выбрано и существующее, и новое.</summary>
    public const string EventRequired = "Выберите событие или заведите новое.";

    /// <inheritdoc cref="LinkPersonsViaEventValidator" />
    public LinkPersonsViaEventValidator()
    {
        RuleFor(c => c.PersonId).GreaterThan(0);
        RuleFor(c => c.OtherPersonId).GreaterThan(0)
            .NotEqual(c => c.PersonId).WithMessage(LinkEventGuard.SelfLink);
        RuleFor(c => c.EventId).GreaterThan(0).When(c => c.EventId is not null);
        RuleFor(c => c).Must(c => (c.EventId is null) != (c.NewEvent is null)).WithMessage(EventRequired);
        RuleFor(c => c.NewEvent!).SetValidator(new LinkEventDraftValidator()).When(c => c.NewEvent is not null);
        RuleFor(c => c.PersonRole).MaximumLength(LinkParticipantDraftValidator.MaxRoleLength);
        RuleFor(c => c.OtherPersonRole).MaximumLength(LinkParticipantDraftValidator.MaxRoleLength);
        RuleFor(c => c.Contact).NotNull().SetValidator(new LinkParticipantDraftValidator());
    }
}

/// <summary>Правила сведений о событии: вид из перечня, название задано, длины в пределах таблицы.</summary>
public sealed class LinkEventDraftValidator : AbstractValidator<LinkEventDraft>
{
    /// <summary>Предел длины названия.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Предел длины описания.</summary>
    public const int MaxDescriptionLength = 4000;

    /// <inheritdoc cref="LinkEventDraftValidator" />
    public LinkEventDraftValidator()
    {
        RuleFor(d => d.Kind).IsInEnum().WithMessage("Неизвестный вид события.");
        RuleFor(d => d.Title)
            .Must(title => !string.IsNullOrWhiteSpace(title)).WithMessage("Укажите название события.")
            .MaximumLength(MaxTitleLength);
        RuleFor(d => d.Description).MaximumLength(MaxDescriptionLength);
        RuleFor(d => d.OccurredOn)
            .Must(date => date is null || date.Value.Year >= 1900).WithMessage("Дата события раньше 1900 года — проверьте ввод.");
    }
}

/// <summary>
/// Правила участия в событии: уровень из перечня, госномер и адрес контакта — если заданы, то с буквами или цифрами
/// (иначе по ним не найти ни пересечения, ни совпадения в поиске, ТО-мат-11), длины в пределах таблицы.
/// </summary>
public sealed class LinkParticipantDraftValidator : AbstractValidator<LinkParticipantDraft>
{
    /// <summary>Предел длины роли в событии.</summary>
    public const int MaxRoleLength = 200;

    /// <summary>Предел длины госномера (как у транспорта фигуранта).</summary>
    public const int MaxPlateLength = 20;

    /// <summary>Предел длины марки и модели.</summary>
    public const int MaxVehicleDetailsLength = 200;

    /// <summary>Предел длины адреса контакта (как у адреса фигуранта).</summary>
    public const int MaxAddressLength = 1000;

    /// <inheritdoc cref="LinkParticipantDraftValidator" />
    public LinkParticipantDraftValidator()
    {
        RuleFor(d => d.Role).MaximumLength(MaxRoleLength);
        RuleFor(d => d.Level).IsInEnum().WithMessage("Неизвестный уровень связи.");
        RuleFor(d => d.VehiclePlate)
            .MaximumLength(MaxPlateLength)
            .Must(plate => string.IsNullOrWhiteSpace(plate) || RequisiteNormalizer.Plate(plate) is not null)
            .WithMessage("Госномер должен содержать буквы или цифры.");
        RuleFor(d => d.VehicleDetails).MaximumLength(MaxVehicleDetailsLength);
        RuleFor(d => d.ContactAddress)
            .MaximumLength(MaxAddressLength)
            .Must(address => string.IsNullOrWhiteSpace(address) || RequisiteNormalizer.Address(address) is not null)
            .WithMessage("Адрес контакта должен содержать буквы или цифры.");
    }
}

/// <summary>Изменить вид, название, дату и описание события (ТФ-ПЕР-11). Ведут роли, ведущие дела.</summary>
/// <param name="EventId">Событие.</param>
/// <param name="Event">Новые сведения.</param>
public sealed record UpdateLinkEventCommand(int EventId, LinkEventDraft Event) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:event:{EventId}:update";

    /// <inheritdoc cref="UpdateLinkEventCommand" />
    public sealed class Handler(
        ILinkEventStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<UpdateLinkEventCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(UpdateLinkEventCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return LinkEventGuard.ToResponse(await store.UpdateEventAsync(command.EventId, command.Event, access, cancellationToken));
        }
    }
}

/// <summary>Правила правки события.</summary>
public sealed class UpdateLinkEventValidator : AbstractValidator<UpdateLinkEventCommand>
{
    /// <inheritdoc cref="UpdateLinkEventValidator" />
    public UpdateLinkEventValidator()
    {
        RuleFor(c => c.EventId).GreaterThan(0);
        RuleFor(c => c.Event).NotNull().SetValidator(new LinkEventDraftValidator());
    }
}

/// <summary>
/// Изменить участие объекта в событии: роль, уровень связи, отметку «в общем поиске», транспорт и адрес контакта
/// (ТФ-ПЕР-11). Только участие фигуранта, доступного субъекту по решётке и роли.
/// </summary>
/// <param name="ParticipantId">Запись участия.</param>
/// <param name="Participation">Новые сведения об участии.</param>
public sealed record UpdateEventParticipantCommand(int ParticipantId, LinkParticipantDraft Participation)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Уровень и отметка — признаки связи, не сведения об объекте: в сводке они помогают разбору.</remarks>
    public string? AuditSummary =>
        $"investigation:event:participant:{ParticipantId}:update;level={Participation?.Level};search={Participation?.InSearch}";

    /// <inheritdoc cref="UpdateEventParticipantCommand" />
    public sealed class Handler(
        ILinkEventStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<UpdateEventParticipantCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(UpdateEventParticipantCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return LinkEventGuard.ToResponse(
                await store.UpdateParticipantAsync(command.ParticipantId, command.Participation, access, cancellationToken));
        }
    }
}

/// <summary>Правила правки участия.</summary>
public sealed class UpdateEventParticipantValidator : AbstractValidator<UpdateEventParticipantCommand>
{
    /// <inheritdoc cref="UpdateEventParticipantValidator" />
    public UpdateEventParticipantValidator()
    {
        RuleFor(c => c.ParticipantId).GreaterThan(0);
        RuleFor(c => c.Participation).NotNull().SetValidator(new LinkParticipantDraftValidator());
    }
}

/// <summary>
/// Исключить объект из события (ошибка ввода, ТФ-ПЕР-11); событие без участников удаляется. Только участие фигуранта,
/// доступного субъекту по решётке и роли; исключение — в журнале.
/// </summary>
/// <param name="ParticipantId">Запись участия.</param>
public sealed record RemoveEventParticipantCommand(int ParticipantId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:event:participant:{ParticipantId}:remove";

    /// <inheritdoc cref="RemoveEventParticipantCommand" />
    public sealed class Handler(
        ILinkEventStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<RemoveEventParticipantCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RemoveEventParticipantCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return LinkEventGuard.ToResponse(await store.RemoveParticipantAsync(command.ParticipantId, access, cancellationToken));
        }
    }
}

/// <summary>Правила исключения участия.</summary>
public sealed class RemoveEventParticipantValidator : AbstractValidator<RemoveEventParticipantCommand>
{
    /// <inheritdoc cref="RemoveEventParticipantValidator" />
    public RemoveEventParticipantValidator()
    {
        RuleFor(c => c.ParticipantId).GreaterThan(0);
    }
}

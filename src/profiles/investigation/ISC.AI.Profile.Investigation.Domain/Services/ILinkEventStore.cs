using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Участник события так, как его видит субъект (ADR-0056 п. 3). Доступный по решётке и роли — полностью; в пределах
/// допуска, но вне дел субъекта по роли — только дело (номер, вид, статус, ведущий), как чужое дело в пересечениях
/// (ТБ-084): ФИО, роль, транспорт и адрес — <see langword="null"/>. Выше допуска — строки нет вовсе.
/// </summary>
/// <param name="Id">Запись участия.</param>
/// <param name="PersonId">Фигурант; <see langword="null"/> — недоступен субъекту по роли.</param>
/// <param name="PersonName">ФИО; <see langword="null"/> — недоступен по роли.</param>
/// <param name="Alias">Псевдоним (анкета); только у доступного.</param>
/// <param name="CaseId">Дело участника.</param>
/// <param name="CaseNumber">Номер дела.</param>
/// <param name="CaseKind">Вид дела.</param>
/// <param name="CaseStatus">Статус дела.</param>
/// <param name="ResponsibleUserId">Ведущий дела.</param>
/// <param name="IsAccessible">Фигурант доступен субъекту по полной решётке и роли (карточка открывается).</param>
/// <param name="Role">Роль в событии; только у доступного.</param>
/// <param name="Level">Уровень связи.</param>
/// <param name="InSearch">Учитывать в общем поиске; у недоступного — <see langword="false"/>.</param>
/// <param name="VehiclePlate">Госномер транспорта контакта; только у доступного.</param>
/// <param name="VehicleDetails">Марка, модель транспорта контакта; только у доступного.</param>
/// <param name="ContactAddress">Адрес контакта; только у доступного.</param>
/// <param name="Classification">Гриф строки участия (фигуранта) — для грифа справки (ТБ-033); на экран не выводится.</param>
public sealed record LinkEventParticipantRow(
    int Id,
    int? PersonId,
    string? PersonName,
    string? Alias,
    int CaseId,
    string CaseNumber,
    CaseKind CaseKind,
    CaseStatus CaseStatus,
    int? ResponsibleUserId,
    bool IsAccessible,
    string? Role,
    LinkLevel Level,
    bool InSearch,
    string? VehiclePlate,
    string? VehicleDetails,
    string? ContactAddress,
    short Classification);

/// <summary>Событие — «дело-посредник» (ТФ-ПЕР-11, ADR-0056) — с видимыми субъекту участниками.</summary>
/// <param name="Id">Событие.</param>
/// <param name="Kind">Вид.</param>
/// <param name="Title">Название.</param>
/// <param name="OccurredOn">Дата события.</param>
/// <param name="Description">Описание.</param>
/// <param name="Classification">Гриф события.</param>
/// <param name="DivisionId">Подразделение события.</param>
/// <param name="CreatedByUserId">Кто завёл.</param>
/// <param name="CreatedAt">Когда заведено (UTC).</param>
/// <param name="Participants">Участники, видимые субъекту: доступные — первыми, затем по делу и записи.</param>
public sealed record LinkEventRow(
    int Id,
    LinkEventKind Kind,
    string Title,
    DateOnly? OccurredOn,
    string? Description,
    short Classification,
    int DivisionId,
    int? CreatedByUserId,
    DateTime CreatedAt,
    IReadOnlyList<LinkEventParticipantRow> Participants);

/// <summary>Сведения о событии из формы: вид, название, дата, описание.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="Title">Название (обязательно).</param>
/// <param name="OccurredOn">Дата.</param>
/// <param name="Description">Описание.</param>
public sealed record LinkEventDraft(LinkEventKind Kind, string Title, DateOnly? OccurredOn, string? Description);

/// <summary>Участие фигуранта в событии из формы: роль, уровень, отметка поиска, транспорт и адрес контакта.</summary>
/// <param name="Role">Роль в событии.</param>
/// <param name="Level">Уровень связи.</param>
/// <param name="InSearch">Учитывать в общем поиске и в пересечениях по местам посещения.</param>
/// <param name="VehiclePlate">Госномер транспорта контакта.</param>
/// <param name="VehicleDetails">Марка, модель, цвет.</param>
/// <param name="ContactAddress">Адрес контакта.</param>
public sealed record LinkParticipantDraft(
    string? Role, LinkLevel Level, bool InSearch, string? VehiclePlate, string? VehicleDetails, string? ContactAddress);

/// <summary>
/// Связать два объекта через событие (ТФ-ПЕР-11): существующее (<paramref name="EventId"/>) или новое
/// (<paramref name="NewEvent"/>) — ровно одно из двух. Новые участники получают <paramref name="Contact"/> (уровень,
/// отметку, транспорт и адрес) и свою роль; объект, уже участвующий в событии, сохраняет свою запись.
/// </summary>
/// <param name="PersonId">Объект, из карточки которого заводится связь.</param>
/// <param name="OtherPersonId">Второй объект.</param>
/// <param name="EventId">Существующее событие.</param>
/// <param name="NewEvent">Новое событие.</param>
/// <param name="PersonRole">Роль первого объекта в событии.</param>
/// <param name="OtherPersonRole">Роль второго объекта в событии.</param>
/// <param name="Contact">Уровень, отметка поиска, транспорт и адрес контакта (без роли — она у каждого своя).</param>
public sealed record LinkPersonsDraft(
    int PersonId,
    int OtherPersonId,
    int? EventId,
    LinkEventDraft? NewEvent,
    string? PersonRole,
    string? OtherPersonRole,
    LinkParticipantDraft Contact);

/// <summary>Итог записи связи через событие.</summary>
public enum LinkEventWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Фигурант, событие или участие не найдены или недоступны (ТБ-021: неотличимо от несуществующего).</summary>
    NotFound = 1,

    /// <summary>Объект связывается сам с собой [АИС табл. 7.1 № 3].</summary>
    SelfLink = 2,

    /// <summary>Оба объекта уже участвуют в этом событии.</summary>
    AlreadyLinked = 3,
}

/// <summary>Событие, найденное единым поиском (ADR-0056 п. 5): событие, участник и его дело.</summary>
/// <param name="EventId">Событие.</param>
/// <param name="Kind">Вид события.</param>
/// <param name="Title">Название.</param>
/// <param name="OccurredOn">Дата.</param>
/// <param name="PersonId">Участник — фигурант, доступный субъекту.</param>
/// <param name="PersonName">ФИО участника.</param>
/// <param name="CaseId">Дело участника.</param>
/// <param name="Role">Роль участника.</param>
/// <param name="ContactAddress">Адрес контакта участника.</param>
/// <param name="VehiclePlate">Госномер контакта участника.</param>
public sealed record EventSearchRow(
    int EventId,
    LinkEventKind Kind,
    string Title,
    DateOnly? OccurredOn,
    int PersonId,
    string PersonName,
    int CaseId,
    string? Role,
    string? ContactAddress,
    string? VehiclePlate);

/// <summary>
/// События — «дела-посредники» связи объектов (ТФ-ПЕР-11, ADR-0056). Чтение и запись — под решёткой субъекта.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТБ-084, ADR-0056 п. 3): событие видно, если оно в пределах допуска (floor ядра и политика) и
/// среди участников есть фигурант, доступный субъекту по полной решётке и роли. Участник видимого события — полностью
/// (доступен по роли), только делом (в пределах допуска, вне дел субъекта) или никак (выше допуска). Недоступное
/// неотличимо от несуществующего; состав запросов не зависит от данных.
/// </remarks>
public interface ILinkEventStore
{
    /// <summary>События фигуранта, видимые субъекту; <see langword="null"/> — фигурант недоступен или не существует.</summary>
    Task<IReadOnlyList<LinkEventRow>?> ListForPersonAsync(int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Событие с видимыми участниками; <see langword="null"/> — недоступно или не существует.</summary>
    Task<LinkEventRow?> GetAsync(int eventId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Связать два объекта через событие. Гриф события поднимается до наибольшего грифа участников; подразделение нового
    /// события — дела первого объекта.
    /// </summary>
    /// <param name="draft">Что связать и через что.</param>
    /// <param name="access">Субъект: оба объекта и существующее событие — под его решёткой.</param>
    /// <param name="userId">Кто заводит (сотрудник ядра).</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<(LinkEventWriteResult Result, int EventId)> LinkAsync(
        LinkPersonsDraft draft, AccessContext access, int? userId, CancellationToken cancellationToken = default);

    /// <summary>Изменить вид, название, дату и описание видимого события.</summary>
    Task<LinkEventWriteResult> UpdateEventAsync(
        int eventId, LinkEventDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Изменить участие доступного по роли фигуранта: роль, уровень, отметку, транспорт и адрес.</summary>
    Task<LinkEventWriteResult> UpdateParticipantAsync(
        int participantId, LinkParticipantDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Исключить участие доступного по роли фигуранта; событие без участников удаляется в той же транзакции.</summary>
    Task<LinkEventWriteResult> RemoveParticipantAsync(int participantId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Единый поиск по событиям (ADR-0056 п. 5): название, описание, роль, адрес и госномер контакта — по участникам с
    /// отметкой «в общем поиске» и уровнем не «неверно», доступным субъекту в делах <paramref name="caseIds"/>.
    /// </summary>
    Task<IReadOnlyList<EventSearchRow>> SearchAsync(
        SearchTerms terms, IReadOnlyCollection<int> caseIds, int limit, AccessContext access, CancellationToken cancellationToken = default);
}

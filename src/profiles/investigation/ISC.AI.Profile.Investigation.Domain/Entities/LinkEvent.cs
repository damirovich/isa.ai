using System;
using System.Collections.Generic;
using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Событие — «дело-посредник» связи объектов (<c>investigation.link_event</c>, ТФ-ПЕР-11, ADR-0056): встреча, контакт,
/// совместная поездка, через которые связаны объекты разных дел. Не дело-задание: у события нет реквизитов задания,
/// ведущего и материалов. Участники — <see cref="LinkEventParticipant"/>.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020, вопрос 26 Приложения В): гриф события не ниже грифа любого участника — хранилище поднимает его
/// при добавлении участника с грифом выше; подразделение — дела объекта, из карточки которого событие заведено, и
/// не меняется. Видно событие только в пределах допуска и при доступном субъекту участнике (ADR-0056 п. 3).
/// </remarks>
public class LinkEvent : AuditableEntity, IClassified
{
    /// <summary>Вид события.</summary>
    public LinkEventKind Kind { get; set; }

    /// <summary>Название («Встреча у рынка "Ош"»).</summary>
    public required string Title { get; set; }

    /// <summary>Дата события (если известна).</summary>
    public DateOnly? OccurredOn { get; set; }

    /// <summary>Описание.</summary>
    public string? Description { get; set; }

    /// <summary>Кто завёл событие (сотрудник ядра); <see langword="null"/> — режим первичной настройки.</summary>
    public int? CreatedByUserId { get; set; }

    /// <summary>Гриф — не ниже грифов участников. NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение — дела объекта, из карточки которого заведено событие. NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>Участники (каскадное удаление с событием).</summary>
    public List<LinkEventParticipant> Participants { get; set; } = [];
}

/// <summary>
/// Участник события (<c>investigation.link_event_participant</c>, ТФ-ПЕР-11, ADR-0056): фигурант, его роль в событии,
/// уровень связи, отметка «учитывать в общем поиске», транспорт и адрес контакта. Пара «событие — фигурант» уникальна.
/// </summary>
/// <remarks>
/// Гриф и подразделение — фигуранта (как у адресов и транспорта, ТБ-070): адрес и госномер контакта — сведения об
/// объекте. Нормализованные значения (ТО-мат-11) — для пересечений по местам посещения и единого поиска; участвуют
/// только при отметке «в общем поиске» и уровне не «неверно» (ADR-0056 п. 5).
/// </remarks>
public class LinkEventParticipant : AuditableEntity, IClassified
{
    /// <summary>Событие (FK; удаление события уносит участника).</summary>
    public int EventId { get; set; }

    /// <summary>Навигация к событию.</summary>
    public LinkEvent? Event { get; set; }

    /// <summary>Фигурант (FK; удаление фигуранта — в том числе уничтожением дела — уносит участие).</summary>
    public int PersonId { get; set; }

    /// <summary>Навигация к фигуранту.</summary>
    public Person? Person { get; set; }

    /// <summary>Роль в событии (свободный текст: «водитель», «организатор»).</summary>
    public string? Role { get; set; }

    /// <summary>Уровень связи: «верно / предположительно / неверно».</summary>
    public LinkLevel Level { get; set; }

    /// <summary>Учитывать в общем поиске и в пересечениях по местам посещения.</summary>
    public bool InSearch { get; set; }

    /// <summary>Госномер транспорта контакта как введён.</summary>
    public string? VehiclePlate { get; set; }

    /// <summary>Нормализованный госномер (ТО-мат-11); <see langword="null"/> — номера нет.</summary>
    public string? VehiclePlateNormalized { get; set; }

    /// <summary>Марка, модель, цвет транспорта контакта.</summary>
    public string? VehicleDetails { get; set; }

    /// <summary>Адрес контакта как введён.</summary>
    public string? ContactAddress { get; set; }

    /// <summary>Нормализованный адрес контакта (ТО-мат-11); <see langword="null"/> — адреса нет.</summary>
    public string? ContactAddressNormalized { get; set; }

    /// <summary>Гриф (с фигуранта). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (с фигуранта). NOT NULL.</summary>
    public int DivisionId { get; set; }
}

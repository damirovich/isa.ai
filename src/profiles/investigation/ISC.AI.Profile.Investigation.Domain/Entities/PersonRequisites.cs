using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Адрес фигуранта (<c>investigation.person_address</c>, ТФ-ПЕР-06): проживание, пребывание, работа.
/// Гриф и подразделение — фигуранта, то есть дела (ТБ-070): адрес виден ровно тогда, когда виден фигурант.
/// Хранится исходное написание и нормализованное (ТО-мат-11) — по нему ищутся пересечения (ТФ-ПЕР-07).
/// </summary>
public class PersonAddress : AuditableEntity, IClassified
{
    /// <summary>Фигурант (FK внутри схемы; удаление фигуранта уносит адрес).</summary>
    public int PersonId { get; set; }

    /// <summary>Навигация к фигуранту.</summary>
    public Person? Person { get; set; }

    /// <summary>Вид адреса.</summary>
    public AddressKind Kind { get; set; }

    /// <summary>Адрес как введён.</summary>
    public required string Text { get; set; }

    /// <summary>Нормализованный адрес (ТО-мат-11).</summary>
    public required string TextNormalized { get; set; }

    /// <summary>Примечание (период, откуда известно и т. п.).</summary>
    public string? Notes { get; set; }

    /// <summary>Гриф (с фигуранта). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (с фигуранта). NOT NULL.</summary>
    public int DivisionId { get; set; }
}

/// <summary>
/// Автотранспорт фигуранта (<c>investigation.person_vehicle</c>, ТФ-ПЕР-06): марка, модель, цвет, госномер.
/// Гриф и подразделение — фигуранта. Госномер хранится как введён и нормализованным (регистр, пробелы,
/// дефисы, кириллица ↔ латиница, ТО-мат-11) — по нему ищутся пересечения (ТФ-ПЕР-07).
/// </summary>
public class PersonVehicle : AuditableEntity, IClassified
{
    /// <summary>Фигурант (FK внутри схемы; удаление фигуранта уносит транспорт).</summary>
    public int PersonId { get; set; }

    /// <summary>Навигация к фигуранту.</summary>
    public Person? Person { get; set; }

    /// <summary>Госномер как введён (может быть неизвестен).</summary>
    public string? PlateNumber { get; set; }

    /// <summary>Нормализованный госномер (ТО-мат-11); пусто, если номер неизвестен.</summary>
    public string? PlateNormalized { get; set; }

    /// <summary>Марка.</summary>
    public string? Make { get; set; }

    /// <summary>Модель.</summary>
    public string? Model { get; set; }

    /// <summary>Цвет.</summary>
    public string? Color { get; set; }

    /// <summary>Примечание.</summary>
    public string? Notes { get; set; }

    /// <summary>Гриф (с фигуранта). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (с фигуранта). NOT NULL.</summary>
    public int DivisionId { get; set; }
}

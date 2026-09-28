using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Фигурант дела (<c>investigation.person</c>, ТФ-ПЕР-01): установочные данные могут быть неизвестны —
/// тогда «неустановленное лицо № N». Гриф/подразделение — дела (ТБ-070).
/// </summary>
public class Person : AuditableEntity, IClassified
{
    /// <summary>Дело (FK внутри схемы).</summary>
    public int CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>ФИО/установочные данные либо «Неустановленное лицо № N».</summary>
    public required string DisplayName { get; set; }

    /// <summary>Личность не установлена.</summary>
    public bool IsUnidentified { get; set; }

    /// <summary>Порядковый номер неустановленного лица в деле.</summary>
    public int? UnidentifiedNumber { get; set; }

    /// <summary>Роль в деле по перечню: объект, связь или иная (ТФ-ПЕР-01).</summary>
    public PersonRole Role { get; set; } = PersonRole.Other;

    /// <summary>Уточнение роли свободным текстом (подозреваемый, свидетель, «брат объекта» и т. п.).</summary>
    public string? RoleInCase { get; set; }

    // --- Анкета (ТФ-ПЕР-05). Все поля необязательны: объект часто известен лишь частично. Поля —
    // параметры будущего точного поиска и пересечений (ТФ-ПСК-01, ТФ-ПЕР-07), поэтому хранятся
    // раздельно, а не одним текстом.

    /// <summary>Дата рождения, если известна полностью.</summary>
    public DateOnly? BirthDate { get; set; }

    /// <summary>Год рождения; при известной дате всегда равен её году (держит хранилище).</summary>
    public int? BirthYear { get; set; }

    /// <summary>Место рождения.</summary>
    public string? BirthPlace { get; set; }

    /// <summary>Место работы.</summary>
    public string? WorkPlace { get; set; }

    /// <summary>Место жительства.</summary>
    public string? Residence { get; set; }

    /// <summary>Пол; <see langword="null"/> — неизвестен.</summary>
    public PersonSex? Sex { get; set; }

    /// <summary>Псевдоним (оперативная кличка) объекта.</summary>
    public string? Alias { get; set; }

    // --- Связь объекта (ТФ-ПЕР-06). Связь — это фигурант с ролью «связь» в том же деле: у него своя
    // карточка, анкета, адреса, транспорт и, при необходимости, эталоны для поиска по лицу. Поля ниже
    // заполняются ТОЛЬКО у роли «связь» (держит хранилище).

    /// <summary>Фигурант того же дела, чьей связью является этот (обычно объект задания).</summary>
    public int? LinkedToPersonId { get; set; }

    /// <summary>Кем приходится — запись справочника вида «тип связи» (ТФ-АДМ-07).</summary>
    public int? LinkTypeId { get; set; }

    // --- Нормализованные реквизиты для пересечений (ТО-мат-11, ТФ-ПЕР-07). Вычисляет хранилище при каждой
    // записи; исходные значения хранятся как введены. Сравнение пересечений — точное по этим полям.

    /// <summary>Нормализованное ФИО (регистр, «ё/е», порядок слов); у неустановленного лица — пусто.</summary>
    public string? NameNormalized { get; set; }

    /// <summary>Нормализованное место жительства из анкеты.</summary>
    public string? ResidenceNormalized { get; set; }

    /// <summary>Примечания следователя.</summary>
    public string? Notes { get; set; }

    /// <summary>Гриф (денормализован с дела). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (денормализовано с дела). NOT NULL.</summary>
    public int DivisionId { get; set; }
}

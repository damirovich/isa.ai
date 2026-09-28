using ISC.AI.Abstractions.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Запись справочника профиля (<c>investigation.reference_item</c>, ТФ-АДМ-07): подразделение-инициатор,
/// звание, должность, тип связи, категория материалов. Режимных сведений не содержит, поэтому без грифа.
/// Не удаляется, а выключается: дела ссылаются на запись внешним ключом внутри схемы, и выключенная
/// запись остаётся читаемой в старых делах, но не предлагается в новых.
/// </summary>
public class ReferenceItem : AuditableEntity
{
    /// <summary>Вид справочника.</summary>
    public ReferenceKind Kind { get; set; }

    /// <summary>Наименование (уникально в пределах вида).</summary>
    public required string Name { get; set; }

    /// <summary>Код (необязателен).</summary>
    public string? Code { get; set; }

    /// <summary>Порядок в списках (звания — по старшинству); при равенстве — по наименованию.</summary>
    public int SortOrder { get; set; }

    /// <summary>Действующая запись (предлагается при вводе).</summary>
    public bool IsActive { get; set; } = true;
}

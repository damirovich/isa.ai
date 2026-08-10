using ISC.AI.Abstractions.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Тип документа — управляемый справочник документооборота (ТЗ СКИД §3.1, §9). Схема <c>docflow</c>.
/// Группа определяет поведение документов типа (§1.4): «Хранение» — только регистрация и хранение,
/// «Исполнение» — назначения по подразделениям со сроками и статусами.
/// </summary>
/// <remarks>
/// Удаление типа не предусмотрено — только деактивация (<see cref="IsActive"/>), поэтому НЕ
/// <c>ISoftDeletable</c>. Инвариант §3.1 «группа не меняется при наличии документов типа» проверяется
/// сценарием (см. <c>IDocumentTypeStore.ChangeGroupAsync</c>).
/// </remarks>
public class DocumentType : AuditableEntity
{
    /// <summary>Наименование типа (уникально в справочнике — индекс БД).</summary>
    public required string Name { get; set; }

    /// <summary>Группа поведения: хранение или исполнение (ТЗ СКИД §1.4).</summary>
    public DocumentGroup Group { get; set; }

    /// <summary>Действующий ли тип: неактивный не предлагается при регистрации новых документов.</summary>
    public bool IsActive { get; set; } = true;
}

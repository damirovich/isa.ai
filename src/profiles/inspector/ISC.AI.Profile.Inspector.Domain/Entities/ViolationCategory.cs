namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Вид нарушения из 2-уровневого расширяемого классификатора (Приложение §4): уровень 1 — сфера/
/// направление (<see cref="ParentId"/> = null), уровень 2 — вид внутри сферы. Ведёт администратор;
/// стартовый набор — из прототипа, наполнение уточняемо и НЕ блокирует разработку. Схема <c>inspector</c>.
/// </summary>
public class ViolationCategory : AuditableEntity
{
    /// <summary>Наименование (сфера — на верхнем уровне, вид — на нижнем).</summary>
    public required string Name { get; set; }

    /// <summary>Родительская сфера (null — сам является сферой верхнего уровня). FK внутри схемы inspector.</summary>
    public int? ParentId { get; set; }

    /// <summary>Навигация к родительской сфере.</summary>
    public ViolationCategory? Parent { get; set; }

    /// <summary>Виды внутри сферы.</summary>
    public ICollection<ViolationCategory> Children { get; set; } = [];
}

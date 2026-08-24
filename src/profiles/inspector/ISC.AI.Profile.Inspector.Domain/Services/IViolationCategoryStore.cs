namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Порт классификатора видов нарушений (Приложение §4): 2 уровня «сфера → вид», ведёт администратор.
/// Стартовый набор сфер — из прототипа, заводится <see cref="SeedDefaultsAsync"/> при пустом
/// классификаторе (идемпотентно): без него экран нарушений встречал бы оператора пустым выбором.
/// </summary>
public interface IViolationCategoryStore
{
    /// <summary>Весь классификатор плоским списком со счётчиками нарушений (дерево строит UI по ParentId).</summary>
    Task<IReadOnlyList<ViolationCategoryNode>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Создаёт сферу (<paramref name="parentId"/> = null) или вид внутри сферы. Название уникально
    /// в пределах уровня; третий уровень запрещён (классификатор двухуровневый по ТЗ).
    /// </summary>
    Task<(ViolationWriteResult Result, int CategoryId)> CreateAsync(
        string name, int? parentId, CancellationToken cancellationToken = default);

    /// <summary>Переименовывает сферу/вид (та же проверка уникальности на уровне).</summary>
    Task<ViolationWriteResult> RenameAsync(int categoryId, string name, CancellationToken cancellationToken = default);

    /// <summary>Удаляет сферу/вид — только если нет ни нарушений, ни дочерних видов (<see cref="ViolationWriteResult.InUse"/>).</summary>
    Task<ViolationWriteResult> DeleteAsync(int categoryId, CancellationToken cancellationToken = default);

    /// <summary>Заводит стартовый набор сфер прототипа, если классификатор пуст; возвращает число созданных.</summary>
    Task<int> SeedDefaultsAsync(CancellationToken cancellationToken = default);
}

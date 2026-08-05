using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Строка справочника типов документов для списков UI (ТЗ СКИД §3.1).</summary>
public sealed record DocumentTypeItem(int Id, string Name, DocumentGroup Group, bool IsActive);

/// <summary>Итог изменяющей операции справочника (для маппинга в ответ сценария).</summary>
public enum DocumentTypeWriteResult
{
    /// <summary>Выполнено.</summary>
    Ok,

    /// <summary>Тип не найден.</summary>
    NotFound,

    /// <summary>Наименование уже занято другим типом (уникальность справочника).</summary>
    NameTaken,

    /// <summary>Смена группы запрещена: по типу уже существуют документы (ТЗ СКИД §3.1).</summary>
    HasDocuments,
}

/// <summary>
/// Порт хранилища справочника типов документов. Порт объявлен в домене модуля, реализация — в слое
/// данных (<c>DocFlowDbContext</c>): слой сценариев (<c>*.Application</c>) на слой данных не ссылается —
/// та же слоистость, что у профиля (ТС-008).
/// </summary>
public interface IDocumentTypeStore
{
    /// <summary>Список типов с необязательными фильтрами по группе и активности; сортировка по имени.</summary>
    Task<IReadOnlyList<DocumentTypeItem>> ListAsync(
        DocumentGroup? group = null, bool? isActive = null, CancellationToken cancellationToken = default);

    /// <summary>Создаёт тип. Возвращает идентификатор либо <c>null</c>, если имя занято.</summary>
    Task<int?> CreateAsync(
        string name, DocumentGroup group, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Меняет наименование и активность типа (группу НЕ трогает — см. <see cref="ChangeGroupAsync"/>).</summary>
    Task<DocumentTypeWriteResult> UpdateAsync(
        int id, string name, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Меняет группу типа. ИНВАРИАНТ (ТЗ СКИД §3.1): запрещено, если по типу уже существуют документы —
    /// иначе у зарегистрированных документов «задним числом» поменялось бы поведение (появились/исчезли
    /// назначения и статусы).
    /// </summary>
    Task<DocumentTypeWriteResult> ChangeGroupAsync(
        int id, DocumentGroup newGroup, CancellationToken cancellationToken = default);
}

using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Доменная служба профиля: смена статуса редакции НПА с материализацией нейтрального флага годности
/// ядра по связкам «редакция ↔ чанк» (Э4-02, ADR-0013, ДОК-04 §6.3). Контракт — в домене; реализация —
/// в слое данных профиля (там есть и контекст inspector, и ядровой порт годности).
/// </summary>
public interface IRevisionStatusMaterializer
{
    /// <summary>
    /// Устанавливает статус редакции и материализует видимость связанных чанков в ядре
    /// (действующая → видна; утратила силу → скрыта). Возвращает число затронутых чанков.
    /// </summary>
    Task<int> SetStatusAsync(int normRevisionId, RevisionStatus status, CancellationToken cancellationToken = default);
}

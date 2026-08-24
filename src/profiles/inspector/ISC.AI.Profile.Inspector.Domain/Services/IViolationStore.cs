namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Порт учёта нарушений (Э5-01, Приложение §4): реестр с отбором, карточка, создание и правка.
/// Порт — в домене, реализация — в слое данных (схема <c>inspector</c>). На этих данных стоят
/// расчёт риска (Приложение §2), Дашборд и будущие Архив/Риски/Мониторинг.
/// </summary>
public interface IViolationStore
{
    /// <summary>Страница реестра с отбором; новые по дате выявления первыми.</summary>
    Task<ViolationPage> ListAsync(ViolationListFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Карточка нарушения; <see langword="null"/> — не найдено.</summary>
    Task<ViolationDetails?> GetAsync(int violationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Заносит нарушение. Категория — ЛЮБОГО уровня классификатора: сфера целиком или
    /// уточняющий вид внутри неё (виды наполняются постепенно, уточнение необязательно).
    /// </summary>
    Task<(ViolationWriteResult Result, int ViolationId)> CreateAsync(
        ViolationDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Правит нарушение целиком (та же проверка категории).</summary>
    Task<ViolationWriteResult> UpdateAsync(
        int violationId, ViolationDraft draft, CancellationToken cancellationToken = default);
}

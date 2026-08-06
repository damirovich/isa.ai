namespace ISC.AI.Abstractions.Security;

/// <summary>Пользователь и его действующий допуск; <see cref="MaxClassification"/> = null — допуска нет.</summary>
public sealed record ClearanceRow(
    int UserId,
    string DisplayName,
    bool IsActive,
    short? MaxClassification,
    IReadOnlyList<int> DivisionScope);

/// <summary>
/// Ведение допусков (<c>core.clearance</c>, ТБ-011/020/021): максимальный гриф и разрешённые
/// подразделения субъекта. Порт ЯДРА — допуск профиле-нейтрален; кто вправе его менять, решает
/// профиль (у «Инспектора» — Администратор, §2.1 ТЗ СКИД).
/// </summary>
/// <remarks>
/// До появления этого порта допуск правился только SQL'ем по живой базе: правка не попадала в
/// неизменяемый журнал (ТБ-030), а несоответствие номеров подразделений справочнику обнаруживалось
/// лишь косвенно — «не могу зарегистрировать документ и не понимаю почему».
/// </remarks>
public interface IClearanceStore
{
    /// <summary>Все активные пользователи с их допуском (у кого его нет — с пустым), по имени.</summary>
    Task<IReadOnlyList<ClearanceRow>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Выдаёт или заменяет допуск пользователя. Пустой <paramref name="divisionScope"/> означает
    /// «ни одного подразделения» (default-deny, ТБ-021), а НЕ «все».
    /// </summary>
    /// <returns><see langword="false"/> — такого активного пользователя нет.</returns>
    Task<bool> SetAsync(
        int userId, short maxClassification, IReadOnlyList<int> divisionScope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Отзывает допуск (мягкое удаление записи). Отзыв действует немедленно для всех операций —
    /// допуск нигде не кэшируется (ТБ-016).
    /// </summary>
    /// <returns><see langword="false"/> — допуска и не было.</returns>
    Task<bool> RevokeAsync(int userId, CancellationToken cancellationToken = default);
}

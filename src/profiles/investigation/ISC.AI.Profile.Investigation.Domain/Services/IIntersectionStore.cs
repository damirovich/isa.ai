using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Пересечение фигуранта с другим делом (ТФ-ПЕР-07). Состав полей — ровно то, что разрешает ТБ-084:
/// номер и вид дела, вид и значение совпавшего реквизита, ответственный сотрудник. ФИО, анкета и материалы
/// чужого фигуранта сюда не входят; несколько фигурантов чужого дела с одним ключом дают одну строку.
/// </summary>
/// <param name="Kind">Вид совпавшего реквизита.</param>
/// <param name="Key">Нормализованный ключ совпадения (ТО-мат-11) — по нему принимается решение.</param>
/// <param name="OwnValue">Значение реквизита на своей стороне, как введено.</param>
/// <param name="OtherValue">Значение реквизита в чужом деле, как введено.</param>
/// <param name="OtherCaseId">Чужое дело.</param>
/// <param name="OtherCaseNumber">Номер чужого дела.</param>
/// <param name="OtherCaseKind">Вид чужого дела.</param>
/// <param name="OtherCaseStatus">Статус чужого дела.</param>
/// <param name="ResponsibleUserId">Ответственный сотрудник (ведущий) чужого дела.</param>
/// <param name="CanOpenCase">Субъект видит чужое дело и по роли (ТФ-ДЕЛ-03) — карточку можно открыть.</param>
/// <param name="Decision">Решение по пересечению, если принято.</param>
/// <param name="DecidedByUserId">Кто принял решение.</param>
/// <param name="DecidedAt">Когда принято решение (UTC).</param>
public sealed record IntersectionRow(
    IntersectionKind Kind,
    string Key,
    string OwnValue,
    string OtherValue,
    int OtherCaseId,
    string OtherCaseNumber,
    CaseKind OtherCaseKind,
    CaseStatus OtherCaseStatus,
    int? ResponsibleUserId,
    bool CanOpenCase,
    IntersectionDecision? Decision,
    int? DecidedByUserId,
    DateTime? DecidedAt);

/// <summary>
/// Пересечения между делами (ТФ-ПЕР-07, ТБ-084, ADR-0029): совпадения нормализованных реквизитов фигуранта
/// с фигурантами ДРУГИХ дел экземпляра. Вычисляются при чтении; хранится только решение человека.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-084, ТБ-020/021): «чужая» сторона ищется среди дел, фигурантов и строк реквизитов, прошедших
/// floor ядра и политику профиля (гриф ≤ допуск, подразделение ∈ разрешённых), но БЕЗ сужения по роли —
/// иначе следователь не увидел бы совпадения с делом коллеги. Дело выше допуска не раскрывается ничем.
/// Исходный фигурант — по полной решётке: недоступный неотличим от несуществующего.
/// </remarks>
public interface IIntersectionStore
{
    /// <summary>Пересечения фигуранта; <see langword="null"/> — фигурант недоступен или не существует.</summary>
    Task<IReadOnlyList<IntersectionRow>?> FindForPersonAsync(
        int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Записать решение по пересечению. <see cref="PersonWriteResult.NotFound"/> — фигурант недоступен или такого
    /// пересечения субъект сейчас не видит (строкой решения нельзя прощупать чужие дела).
    /// </summary>
    Task<PersonWriteResult> ReviewAsync(
        int personId,
        IntersectionKind kind,
        string key,
        int otherCaseId,
        IntersectionDecision decision,
        AccessContext access,
        CancellationToken cancellationToken = default);
}

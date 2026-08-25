namespace ISC.AI.Profile.Inspector.Domain.Services;

// Контракты СОВЕЩАНИЙ (§5.2.7, ТФ-СОВ-01/02): протокол — документ группы «Исполнение»
// документооборота, его пункты — назначения со сроками и статусами. Профильные контракты
// не тянут перечислений чужого модуля: статусы приходят ПОДПИСЯМИ (как InspectionDocumentCard).

/// <summary>Пункт протокола (назначение docflow) — подразделение, срок, статус подписью.</summary>
public sealed record MeetingItem(
    int AssignmentId,
    string DivisionName,
    DateOnly? Deadline,
    string StatusLabel,
    bool IsDone,
    bool IsOverdue);

/// <summary>Протокол с пунктами — для справки об исполнении.</summary>
public sealed record MeetingProtocol(
    int DocumentId,
    string? RegNumber,
    DateOnly RegDate,
    string ShortContent,
    short Classification,
    IReadOnlyList<MeetingItem> Items);

/// <summary>
/// Читатель протокола из документооборота ОТ ИМЕНИ субъекта: недоступный по решётке документ —
/// <see langword="null"/>, неотличимо от несуществующего (ТБ-020-стиль). Кросс-модульная склейка
/// (документ + имена подразделений) живёт в слое данных профиля, как остальные docflow-швы.
/// </summary>
public interface IMeetingProtocolReader
{
    /// <summary>Протокол с пунктами; <see langword="null"/> — не найден или недоступен.</summary>
    Task<MeetingProtocol?> ReadAsync(int documentId, CancellationToken cancellationToken = default);
}

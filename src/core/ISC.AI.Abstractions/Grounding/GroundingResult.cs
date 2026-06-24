namespace ISC.AI.Abstractions.Grounding;

/// <summary>Результат грунтовки: классификация каждой ссылки вывода (ТБ-040).</summary>
/// <param name="Citations">Проверки по каждой найденной ссылке.</param>
/// <param name="AllConfirmed">
/// Все ли ссылки подтверждены (нет <see cref="CitationStatus.Unverified"/>/<see cref="CitationStatus.Superseded"/>).
/// Если нет — вывод НЕ выдаётся как готовый (GATE-2).
/// </param>
public sealed record GroundingResult(IReadOnlyList<CitationCheck> Citations, bool AllConfirmed);

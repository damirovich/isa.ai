using ISC.AI.Abstractions.Retrieval;

namespace ISC.AI.Abstractions.Grounding;

/// <summary>
/// Валидатор грунтовки вывода модели против фактически извлечённых фрагментов (ТБ-040).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ БЕЗОПАСНОСТИ (ТБ-040, ADR-0005): ни одна ссылка, ОТСУТСТВУЮЩАЯ в извлечённых фрагментах
/// ИЛИ опирающаяся на НЕАКТУАЛЬНЫЙ источник (<c>IsCurrent = false</c>), не выдаётся как подтверждённая —
/// помечается непроверенной/утратившей силу. Контракт и правило — в ядре; профиль НЕ может их ослабить
/// (ТБ-041). Метод строже строкового совпадения; доменные извлечение и нормализацию ссылок поставляет
/// профиль через <see cref="ICitationExtractor"/>/<see cref="ICitationNormalizer"/> (ADR-0008/0013).
/// Проверяется блокирующим gate-критерием GATE-2.
/// </remarks>
public interface IGroundingValidator
{
    /// <summary>Классифицирует ссылки вывода относительно фактически извлечённых фрагментов.</summary>
    GroundingResult Validate(string output, IReadOnlyList<RetrievedChunk> retrievedContext);
}

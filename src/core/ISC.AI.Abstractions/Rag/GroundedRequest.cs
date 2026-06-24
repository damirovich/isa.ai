using ISC.AI.Abstractions.Enums;

namespace ISC.AI.Abstractions.Rag;

/// <summary>Запрос к RAG-конвейеру (ТО-мат-01).</summary>
/// <param name="Query">Текст запроса пользователя.</param>
/// <param name="Role">Роль модели генерации (по умолчанию — анализ/длинный контекст).</param>
/// <param name="TopK">Сколько фрагментов извлекать (после фильтра доступа).</param>
/// <param name="TaskPrompt">
/// Задачный промпт профиля (после системного правила грунтовки ядра; не отменяет его — ТБ-041).
/// </param>
public sealed record GroundedRequest(
    string Query,
    ModelRole Role = ModelRole.Analysis,
    int TopK = 10,
    string? TaskPrompt = null);

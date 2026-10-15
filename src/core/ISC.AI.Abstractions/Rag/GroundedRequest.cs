using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Retrieval;

namespace ISC.AI.Abstractions.Rag;

/// <summary>Запрос к RAG-конвейеру (ТО-мат-01).</summary>
/// <param name="Query">Текст запроса пользователя.</param>
/// <param name="Role">Роль модели генерации (по умолчанию — анализ/длинный контекст).</param>
/// <param name="TopK">Сколько фрагментов извлекать (после фильтра доступа).</param>
/// <param name="TaskPrompt">
/// Задачный промпт профиля (после системного правила грунтовки ядра; не отменяет его — ТБ-041).
/// </param>
/// <param name="History">
/// История диалога для многоходового общения (предыдущие реплики). Передаётся в модель как контекст ПОСЛЕ
/// системных правил и ДО текущего запроса; грунтовка нового ответа по-прежнему выполняется на СВЕЖИХ
/// фрагментах текущего запроса. <see langword="null"/> — одноходовый запрос (без истории).
/// </param>
/// <param name="Filter">
/// Ограничения извлечения (например, область — документы одного дела, ADR-0051). Только сужают выдачу: фильтр доступа
/// ядро применяет в любом случае (ТБ-020). <see langword="null"/> — весь доступный корпус, только актуальные источники.
/// </param>
public sealed record GroundedRequest(
    string Query,
    ModelRole Role = ModelRole.Analysis,
    int TopK = 10,
    string? TaskPrompt = null,
    IReadOnlyList<ChatTurn>? History = null,
    RetrievalFilter? Filter = null);

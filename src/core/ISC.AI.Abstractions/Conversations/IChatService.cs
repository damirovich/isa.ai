using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Abstractions.Conversations;

/// <summary>Запрос реплики в чат.</summary>
/// <param name="ConversationId">Идентификатор диалога; <see langword="null"/> — начать НОВЫЙ диалог.</param>
/// <param name="Text">Текст реплики пользователя.</param>
/// <param name="Mode">Режим: <see cref="ChatMode.Free"/> — свободное общение; <see cref="ChatMode.Grounded"/> — по НПА/документ.</param>
/// <param name="Role">Роль модели генерации.</param>
/// <param name="TopK">Сколько фрагментов извлекать (после фильтра доступа; только в грунтованном режиме).</param>
/// <param name="TaskPrompt">Задачный промпт профиля (не отменяет системное правило грунтовки — ТБ-041).</param>
public sealed record ChatMessageRequest(
    int? ConversationId,
    string Text,
    ChatMode Mode = ChatMode.Grounded,
    // Диалог — быстрая роль Draft (ADR-0011): собеседник ждёт ответ, а не наблюдает раздумья.
    ModelRole Role = ModelRole.Draft,
    int TopK = 10,
    string? TaskPrompt = null);

/// <summary>Ответ ассистента в диалоге.</summary>
/// <param name="ConversationId">Идентификатор диалога (для нового — присвоенный).</param>
/// <param name="Answer">Текст ответа.</param>
/// <param name="Grounding">Итог грунтовки (статусы ссылок на НПА); <see langword="null"/> в свободном режиме (не сверялось).</param>
/// <param name="Classification">Гриф ответа (максимум грифов использованных фрагментов; 0 в свободном режиме).</param>
/// <param name="UsedFragments">
/// Фрагменты, на которых построен ответ (для ссылок на документы-источники в UI, Э4-35 этап 7.2) —
/// <see langword="null"/> в свободном режиме (извлечение не выполнялось). Ядро не интерпретирует
/// <see cref="RetrievedChunk.Metadata"/> — доменный смысл (напр. <c>docflow_document_id</c>) знает
/// только слой, который решает, куда вести ссылку.
/// </param>
public sealed record ChatReply(
    int ConversationId,
    string Answer,
    GroundingResult? Grounding,
    short Classification,
    IReadOnlyList<RetrievedChunk>? UsedFragments = null);

/// <summary>
/// Обновление потокового ответа чата. Промежуточные — сырой ЧЕРНОВИК по кускам (<see cref="TextDelta"/>);
/// финальное — одно обновление с <see cref="Final"/> (итог грунтовки и подтверждённые ссылки на СОБРАННОМ
/// тексте). Черновик до финала — непроверенный (согласуется с HITL).
/// </summary>
/// <param name="TextDelta">Очередной кусок черновика (для промежуточных обновлений); иначе <see langword="null"/>.</param>
/// <param name="Final">Итог (в терминальном обновлении); иначе <see langword="null"/>.</param>
public sealed record ChatStreamUpdate(string? TextDelta, ChatReply? Final);

/// <summary>
/// Грунтованный многоходовый ассистент: связывает историю диалога и грунтованную генерацию. КАЖДЫЙ ответ
/// по-прежнему проходит фильтр доступа, грунтовку и аудит (режимные инварианты не обходятся) — чат добавляет
/// лишь контекст диалога и сохранение истории.
/// </summary>
public interface IChatService
{
    /// <summary>
    /// Отправляет реплику в диалог (создавая новый при <c>ConversationId = null</c>): загружает историю,
    /// генерирует грунтованный ответ с учётом контекста, сохраняет реплику пользователя и ответ ассистента.
    /// </summary>
    Task<ChatReply> SendAsync(
        ChatMessageRequest request, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Потоковый вариант <see cref="SendAsync"/>: реплика пользователя сохраняется сразу, ответ отдаётся
    /// ЧЕРНОВИКОМ по кускам (<see cref="ChatStreamUpdate.TextDelta"/>), а по завершении — одно терминальное
    /// обновление с <see cref="ChatStreamUpdate.Final"/> (грунтовка на собранном тексте), после чего ответ
    /// ассистента сохраняется в историю. Режимные инварианты — те же, что в блокирующем пути.
    /// </summary>
    IAsyncEnumerable<ChatStreamUpdate> SendStreamingAsync(
        ChatMessageRequest request, AccessContext access, CancellationToken cancellationToken = default);
}

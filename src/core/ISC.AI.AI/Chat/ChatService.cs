using System.Runtime.CompilerServices;
using System.Text.Json;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.AI.Chat;

/// <summary>
/// Грунтованный многоходовый ассистент (<see cref="IChatService"/>): загружает историю диалога, вызывает
/// грунтованную генерацию с контекстом и сохраняет обе реплики. Режимные инварианты внутри генератора:
/// фильтр доступа, обязательная грунтовка, аудит генерации — чат их не обходит.
/// </summary>
public sealed class ChatService(IGroundedGenerator generator, IConversationStore conversations) : IChatService
{
    /// <inheritdoc />
    public async Task<ChatReply> SendAsync(
        ChatMessageRequest request, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);

        // Диалог требует идентифицированного владельца (числовой субъект) — историю нужно к кому-то привязать.
        if (access.NumericSubjectId is not { } subjectId)
        {
            throw new InvalidOperationException("Чат доступен только идентифицированному пользователю (субъекту).");
        }

        // Новый диалог (заголовок — из первого запроса) или существующий.
        var conversationId = request.ConversationId
            ?? await conversations.CreateAsync(subjectId, request.Text, cancellationToken);

        // История ПРЕДЫДУЩИХ реплик (для нового диалога — пусто; для чужого — тоже пусто, разграничение).
        var history = await conversations.GetHistoryAsync(conversationId, subjectId, cancellationToken);

        // Реплика пользователя в журнал диалога. Fail-closed: если диалог субъекту не принадлежит — отказ
        // ДО обращения к модели (чужой диалог недоступен, история его не подмешивается).
        var appended = await conversations.AppendMessageAsync(
            conversationId, subjectId, ConversationMessageRole.User, request.Text,
            classification: 0, groundingJson: null, cancellationToken);
        if (!appended)
        {
            throw new InvalidOperationException("Диалог не найден или недоступен.");
        }

        // Грунтованная генерация с учётом истории (фильтр доступа/грунтовка/аудит — внутри генератора).
        var response = await generator.GenerateAsync(
            new GroundedRequest(request.Text, request.Role, request.TopK, request.TaskPrompt, history),
            access,
            cancellationToken);

        // Ответ ассистента в журнал диалога — с грифом и итогом грунтовки (для отображения статусов ссылок).
        await conversations.AppendMessageAsync(
            conversationId, subjectId, ConversationMessageRole.Assistant, response.Answer,
            response.ResultClassification, JsonSerializer.Serialize(response.Grounding), cancellationToken);

        return new ChatReply(conversationId, response.Answer, response.Grounding, response.ResultClassification);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamUpdate> SendStreamingAsync(
        ChatMessageRequest request, AccessContext access, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);

        if (access.NumericSubjectId is not { } subjectId)
        {
            throw new InvalidOperationException("Чат доступен только идентифицированному пользователю (субъекту).");
        }

        var conversationId = request.ConversationId
            ?? await conversations.CreateAsync(subjectId, request.Text, cancellationToken);

        var history = await conversations.GetHistoryAsync(conversationId, subjectId, cancellationToken);

        var appended = await conversations.AppendMessageAsync(
            conversationId, subjectId, ConversationMessageRole.User, request.Text,
            classification: 0, groundingJson: null, cancellationToken);
        if (!appended)
        {
            throw new InvalidOperationException("Диалог не найден или недоступен.");
        }

        // Стриминг черновика по кускам; грунтовка/аудит — внутри генератора на СОБРАННОМ полном тексте (Final).
        GroundedResponse? final = null;
        await foreach (var update in generator.GenerateStreamingAsync(
            new GroundedRequest(request.Text, request.Role, request.TopK, request.TaskPrompt, history),
            access, cancellationToken))
        {
            if (update.Final is { } finalResponse)
            {
                final = finalResponse;
            }
            else if (!string.IsNullOrEmpty(update.TextDelta))
            {
                yield return new ChatStreamUpdate(update.TextDelta, Final: null);
            }
        }

        // Поток оборвался до финализации (сбой/отмена) — генератор уже записал попытку в аудит; ответ не сохраняем.
        if (final is null)
        {
            yield break;
        }

        // Полный ответ ассистента (грунтованный) — в историю; терминальное обновление с итогом.
        await conversations.AppendMessageAsync(
            conversationId, subjectId, ConversationMessageRole.Assistant, final.Answer,
            final.ResultClassification, JsonSerializer.Serialize(final.Grounding), cancellationToken);

        yield return new ChatStreamUpdate(
            TextDelta: null,
            Final: new ChatReply(conversationId, final.Answer, final.Grounding, final.ResultClassification));
    }
}

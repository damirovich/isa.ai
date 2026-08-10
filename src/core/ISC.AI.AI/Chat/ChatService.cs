using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.AI.Chat;

/// <summary>
/// Многоходовый ассистент чата (<see cref="IChatService"/>) с двумя режимами: СВОБОДНЫЙ
/// (<see cref="ChatMode.Free"/> — общение/помощь без грунтовки, но и без юр-утверждений) и ГРУНТОВАННЫЙ
/// (<see cref="ChatMode.Grounded"/> — по НПА/документ: фильтр доступа → грунтовка → аудит). Загружает историю,
/// сохраняет обе реплики. Режимные инварианты грунтованного пути внутри генератора — чат их не обходит.
/// </summary>
public sealed class ChatService(
    IGroundedGenerator generator,
    IConversationalGenerator conversationalGenerator,
    IConversationStore conversations) : IChatService
{
    /// <inheritdoc />
    public async Task<ChatReply> SendAsync(
        ChatMessageRequest request, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);

        var (conversationId, subjectId, history) = await PrepareConversationAsync(request, access, cancellationToken);

        // Грунтованная генерация с учётом истории (фильтр доступа/грунтовка/аудит — внутри генератора).
        var response = await generator.GenerateAsync(
            new GroundedRequest(request.Text, request.Role, request.TopK, request.TaskPrompt, history),
            access,
            cancellationToken);

        // Ответ ассистента в журнал диалога — с грифом и итогом грунтовки (для отображения статусов ссылок).
        await conversations.AppendMessageAsync(
            conversationId, subjectId, ConversationMessageRole.Assistant, response.Answer,
            response.ResultClassification, JsonSerializer.Serialize(response.Grounding), cancellationToken);

        return new ChatReply(
            conversationId, response.Answer, response.Grounding, response.ResultClassification, response.UsedFragments);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamUpdate> SendStreamingAsync(
        ChatMessageRequest request, AccessContext access, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);

        var (conversationId, subjectId, history) = await PrepareConversationAsync(request, access, cancellationToken);

        // СВОБОДНЫЙ режим: обычный ассистент без извлечения/грунтовки (правило запрещает юр-утверждения).
        if (request.Mode == ChatMode.Free)
        {
            var freeText = new StringBuilder();
            await foreach (var delta in conversationalGenerator.GenerateStreamingAsync(
                request.Text, history, access, request.Role, cancellationToken))
            {
                freeText.Append(delta);
                yield return new ChatStreamUpdate(delta, Final: null);
            }

            var answer = freeText.ToString();
            // Ответ без грунтовки (гриф 0) — в историю; в терминале Grounding=null («не сверено с НПА»).
            await conversations.AppendMessageAsync(
                conversationId, subjectId, ConversationMessageRole.Assistant, answer,
                classification: 0, groundingJson: null, cancellationToken);

            yield return new ChatStreamUpdate(
                TextDelta: null, Final: new ChatReply(conversationId, answer, Grounding: null, Classification: 0));
            yield break;
        }

        // ГРУНТОВАННЫЙ режим. Стриминг черновика по кускам; грунтовка/аудит — на СОБРАННОМ полном тексте (Final).
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
            Final: new ChatReply(
                conversationId, final.Answer, final.Grounding, final.ResultClassification, final.UsedFragments));
    }

    /// <summary>
    /// Общая преамбула обоих путей (обычного и потокового): владелец, диалог, история, реплика
    /// пользователя. Была продублирована в двух методах — инвариантная логика разграничения
    /// расходиться не должна.
    /// </summary>
    /// <remarks>
    /// Fail-closed: диалог требует идентифицированного владельца (числовой субъект — историю нужно
    /// к кому-то привязать); чужой диалог недоступен — история не подмешивается
    /// (<c>GetHistoryAsync</c> для чужого возвращает пусто), а запись реплики отклоняется ДО
    /// обращения к модели.
    /// </remarks>
    private async Task<(int ConversationId, int SubjectId, IReadOnlyList<ChatTurn> History)>
        PrepareConversationAsync(
            ChatMessageRequest request, AccessContext access, CancellationToken cancellationToken)
    {
        if (access.NumericSubjectId is not { } subjectId)
        {
            throw new InvalidOperationException("Чат доступен только идентифицированному пользователю (субъекту).");
        }

        // Новый диалог (заголовок — из первого запроса) или существующий.
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

        return (conversationId, subjectId, history);
    }
}

using System.Runtime.CompilerServices;
using System.Text;
using ISC.AI.AI.Rag;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.AI.Chat;

/// <summary>
/// Свободный режим чата (<see cref="IConversationalGenerator"/>): модель отвечает как обычный ассистент с
/// учётом истории, БЕЗ извлечения НПА и БЕЗ грунтовки. Системное правило (<see cref="ConversationalPrompt"/>)
/// запрещает юридические утверждения и ссылки «по памяти». Генерация аудируется (ТБ-030) с грифом 0 —
/// обращения к ДСП нет. Инвариант грунтовки не затрагивается: любые нормы/документы — только грунтованным путём.
/// </summary>
public sealed class ConversationalGenerator(
    IAuditWriter auditWriter, IServiceProvider serviceProvider, GenerationOptions generation) : IConversationalGenerator
{
    /// <inheritdoc />
    public async IAsyncEnumerable<string> GenerateStreamingAsync(
        string query,
        IReadOnlyList<ChatTurn> history,
        AccessContext access,
        ModelRole role,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(access);

        var chatClient = serviceProvider.GetRequiredKeyedService<IChatClient>(role);
        var messages = BuildMessages(query, history);

        var assembled = new StringBuilder();
        var completed = false;
        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, BuildChatOptions(), cancellationToken))
            {
                var delta = update.Text;
                if (!string.IsNullOrEmpty(delta))
                {
                    assembled.Append(delta);
                    yield return delta;
                }
            }

            // Аудит свободной генерации (ТБ-030): гриф 0 (нет обращения к ДСП), без грунтовки.
            await AuditAsync(access, query, assembled.ToString(), completed: true);
            completed = true;
        }
        finally
        {
            // Поток прерван до завершения (сбой/отмена/ранний выход) — фиксируем попытку.
            if (!completed)
            {
                await AuditAsync(access, query, assembled.ToString(), completed: false);
            }
        }
    }

    // Параметры генерации те же, что у грунтованного пути (детерминизм/границы вывода).
    private ChatOptions BuildChatOptions() => new()
    {
        Temperature = generation.Temperature,
        TopP = generation.TopP,
        MaxOutputTokens = generation.MaxOutputTokens,
        Seed = generation.Seed,
    };

    private static List<ChatMessage> BuildMessages(string query, IReadOnlyList<ChatTurn> history)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ConversationalPrompt.SystemRule), // свободное правило — первым
        };

        if (history is { Count: > 0 })
        {
            foreach (var turn in history)
            {
                var chatRole = turn.Role == ConversationMessageRole.User ? ChatRole.User : ChatRole.Assistant;
                messages.Add(new ChatMessage(chatRole, turn.Text));
            }
        }

        messages.Add(new ChatMessage(ChatRole.User, query));
        return messages;
    }

    // CancellationToken.None: запись в неизменяемый журнал обязана появиться даже при отмене исходного вызова.
    private Task AuditAsync(AccessContext access, string query, string answer, bool completed) =>
        auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Generate,
                Classification: 0,
                SubjectId: access.NumericSubjectId,
                ObjectRef: null,
                PayloadSensitive: (completed ? "Свободный чат (без грунтовки)." : "Свободный чат НЕ завершён.")
                    + $" Запрос: {query}\nОтвет: {answer}"),
            CancellationToken.None);
}

using ISC.AI.AI.Grounding;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.AI.Rag;

/// <summary>
/// RAG-оркестратор (ТО-мат-01): запрос → извлечение с фильтром доступа → сборка промпта → генерация →
/// грунтовка → результат. Связывает <see cref="IRetriever"/>, <see cref="IChatClient"/> (по роли) и
/// <see cref="IGroundingValidator"/> в единый конвейер; пишет аудит генерации (ТБ-030).
/// </summary>
/// <remarks>
/// Режимные инварианты: модель получает ТОЛЬКО отфильтрованные фрагменты (фильтр доступа применён в
/// retriever ДО неё — ТБ-020); системное правило грунтовки ставится ПЕРВЫМ и профилем не отменяется
/// (ТБ-041); грунтовка вывода обязательна (ТБ-040, GATE-2); итоговый гриф = максимум грифов
/// использованных фрагментов (наследование, ТБ-032/033).
/// </remarks>
public sealed class GroundedGenerator(
    IRetriever retriever,
    IGroundingValidator groundingValidator,
    IAuditWriter auditWriter,
    IServiceProvider serviceProvider) : IGroundedGenerator
{
    /// <inheritdoc />
    public async Task<GroundedResponse> GenerateAsync(
        GroundedRequest request,
        AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);

        // 1–3. Извлечение с ОБЯЗАТЕЛЬНЫМ фильтром доступа (ТБ-020, fail-closed) + только актуальные.
        var fragments = await retriever.RetrieveAsync(request.Query, access, request.TopK, filter: null, cancellationToken);

        // 4. Промпт: системный (ядро, грунтовка) ПЕРВЫМ + задачный (профиль) + фрагменты + запрос (ТБ-041).
        var chatClient = serviceProvider.GetRequiredKeyedService<IChatClient>(request.Role);
        var messages = BuildMessages(request, fragments);

        // 5. Генерация. Модель получает только то, что вернул фильтрованный retriever.
        var modelResponse = await chatClient.GetResponseAsync(messages, options: null, cancellationToken);
        var answer = modelResponse.Text ?? string.Empty;

        // 6. Грунтовка вывода против извлечённых фрагментов (ТБ-040, GATE-2).
        var grounding = groundingValidator.Validate(answer, fragments);

        // 7. Итоговый гриф = максимум грифов использованных фрагментов (наследование, ТБ-032/033).
        short resultClassification = 0;
        foreach (var fragment in fragments)
        {
            if (fragment.Classification > resultClassification)
            {
                resultClassification = fragment.Classification;
            }
        }

        // Аудит генерации (ТБ-030): метаданные (id фрагментов) отдельно от чувствительного (запрос/ответ, ТБ-032).
        var usedChunkIds = string.Join(",", fragments.Select(f => f.ChunkId));
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Generate,
                resultClassification,
                ObjectRef: usedChunkIds,
                PayloadSensitive: $"Запрос: {request.Query}\nОтвет: {answer}"),
            cancellationToken);

        return new GroundedResponse(answer, grounding, fragments, resultClassification);
    }

    private static List<ChatMessage> BuildMessages(GroundedRequest request, IReadOnlyList<RetrievedChunk> fragments)
    {
        var messages = new List<ChatMessage>
        {
            // Неотключаемое правило грунтовки ядра — ПЕРВЫМ (ТБ-041).
            new(ChatRole.System, GroundingPrompt.SystemRule),
        };

        if (!string.IsNullOrWhiteSpace(request.TaskPrompt))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.TaskPrompt));
        }

        var context = string.Join(
            "\n\n",
            fragments.Select((f, index) => $"[Фрагмент {index + 1}]\n{f.Text}"));

        messages.Add(new ChatMessage(
            ChatRole.User,
            $"Фрагменты:\n{context}\n\nЗапрос: {request.Query}"));

        return messages;
    }
}

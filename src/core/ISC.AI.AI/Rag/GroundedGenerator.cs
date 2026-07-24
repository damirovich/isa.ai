using System.Runtime.CompilerServices;
using System.Text;
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
        ChatResponse modelResponse;
        try
        {
            modelResponse = await chatClient.GetResponseAsync(messages, options: null, cancellationToken);
        }
        catch (Exception exception)
        {
            // ТБ-030: retrieval уже прочитал защищённые фрагменты под допуском субъекта — фиксируем ПОПЫТКУ
            // в неизменяемом журнале даже при сбое/отмене модели («данные могли быть уже извлечены»), затем
            // пробрасываем. Иначе обращение к ДСП внутри упавшей генерации осталось бы вне журнала.
            await AuditFailedAttemptAsync(request, fragments, access, exception.GetType().Name);
            throw;
        }

        var answer = modelResponse.Text ?? string.Empty;

        // 6–7 + аудит: грунтовка на полном тексте, наследование грифа, запись в журнал (общий хвост).
        return await FinalizeAsync(request, fragments, answer, access, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<GroundedStreamUpdate> GenerateStreamingAsync(
        GroundedRequest request,
        AccessContext access,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(access);

        // 1–3. Извлечение с ОБЯЗАТЕЛЬНЫМ фильтром доступа (ТБ-020, fail-closed) — как и в блокирующем пути.
        var fragments = await retriever.RetrieveAsync(request.Query, access, request.TopK, filter: null, cancellationToken);

        // 4. Тот же промпт: системный (грунтовка) ПЕРВЫМ + задачный + фрагменты (ТБ-041).
        var chatClient = serviceProvider.GetRequiredKeyedService<IChatClient>(request.Role);
        var messages = BuildMessages(request, fragments);

        // 5. Стриминг СЫРОГО черновика: отдаём токены по мере генерации, параллельно копим полный текст.
        //    Грунтовка на промежуточных токенах НЕ выполняется — только на собранном тексте (ТБ-040).
        var assembled = new StringBuilder();
        var finalized = false;
        try
        {
            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options: null, cancellationToken))
            {
                var delta = update.Text;
                if (!string.IsNullOrEmpty(delta))
                {
                    assembled.Append(delta);
                    yield return new GroundedStreamUpdate(TextDelta: delta, Final: null);
                }
            }

            // 6–7 + аудит: грунтовка на ПОЛНОМ тексте, наследование грифа, запись в журнал — итог ОДНИМ
            //    последним обновлением (только он несёт вердикт грунтовки и подтверждённые ссылки).
            var final = await FinalizeAsync(request, fragments, assembled.ToString(), access, cancellationToken);
            finalized = true;
            yield return new GroundedStreamUpdate(TextDelta: null, Final: final);
        }
        finally
        {
            // ТБ-030: если поток прервался ДО финализации (сбой/отмена модели или ранний выход потребителя),
            // успешный аудит FinalizeAsync не достигнут — фиксируем попытку (ДСП уже извлечён). yield с catch
            // недопустим, поэтому исключение здесь недоступно — пишем нейтральную причину. finalized → успех уже записан.
            if (!finalized)
            {
                await AuditFailedAttemptAsync(request, fragments, access, "поток прерван до завершения");
            }
        }
    }

    // Общий «хвост» обоих путей: грунтовка вывода (ТБ-040), наследование грифа (ТБ-032/033) и запись
    // единственного аудита генерации (ТБ-030). Вынесен, чтобы блокирующий и потоковый пути не расходились.
    private async Task<GroundedResponse> FinalizeAsync(
        GroundedRequest request,
        IReadOnlyList<RetrievedChunk> fragments,
        string answer,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        // 6. Грунтовка вывода против извлечённых фрагментов (ТБ-040, GATE-2).
        var grounding = groundingValidator.Validate(answer, fragments);

        // 7. Итоговый гриф = максимум грифов использованных фрагментов (наследование, ТБ-032/033).
        var resultClassification = MaxClassification(fragments);

        // Аудит генерации (ТБ-030 «кто/что/когда»): субъект — из контекста доступа (единственная запись
        // события; команда намеренно не IAuditableRequest, чтобы не задвоить журнал). Метаданные (id
        // фрагментов) отдельно от чувствительного (запрос/ответ, ТБ-032).
        var usedChunkIds = string.Join(",", fragments.Select(f => f.ChunkId));
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Generate,
                resultClassification,
                SubjectId: access.NumericSubjectId,
                ObjectRef: usedChunkIds,
                PayloadSensitive: $"Запрос: {request.Query}\nОтвет: {answer}"),
            cancellationToken);

        return new GroundedResponse(answer, grounding, fragments, resultClassification);
    }

    // Итоговый гриф результата = максимум грифов использованных фрагментов (наследование, ТБ-032/033).
    private static short MaxClassification(IReadOnlyList<RetrievedChunk> fragments)
    {
        short max = 0;
        foreach (var fragment in fragments)
        {
            if (fragment.Classification > max)
            {
                max = fragment.Classification;
            }
        }

        return max;
    }

    // ТБ-030: запись ПОПЫТКИ генерации в неизменяемый журнал при сбое/отмене (retrieval уже прочитал ДСП).
    // CancellationToken.None: запись обязана появиться даже когда исходный вызов отменён.
    private Task AuditFailedAttemptAsync(
        GroundedRequest request, IReadOnlyList<RetrievedChunk> fragments, AccessContext access, string reason) =>
        auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Generate,
                MaxClassification(fragments),
                SubjectId: access.NumericSubjectId,
                ObjectRef: string.Join(",", fragments.Select(f => f.ChunkId)),
                PayloadSensitive: $"Генерация НЕ завершена: {reason}. Запрос: {request.Query}"),
            CancellationToken.None);

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

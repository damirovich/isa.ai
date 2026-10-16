using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Application.Answers;
using ISC.AI.Modules.Chat.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Chat.Application.Features;

/// <summary>
/// Вопрос в чат (ADR-0052). Режим по материалам: поиск фрагментов только в материалах области под фильтром доступа ядра,
/// ответ общим грунтованным конвейером ядра, проверка ссылок, сохранение в диалоге с источниками. Свободный режим (если
/// профиль разрешил): ассистент ядра без поиска и грунтовки. Ответ отдаётся потоком.
/// </summary>
/// <remarks>
/// <para>ИНВАРИАНТЫ. Область и её материалы даёт профиль под своими решётками (<see cref="IChatProfile"/>); поверх неё ядро
/// применяет floor допуска к фрагментам (ТБ-020). Правило грунтовки ядра идёт первым, промпт профиля — вторым и его не
/// ослабляет (ТБ-041). Ссылки ответа проверяет ядровой валидатор по поданным фрагментам (ТБ-040, инвариант 1), номера
/// «[Фрагмент N]» — <see cref="FragmentReferences"/>; непроверенное показывается с предупреждением, ответ — черновик
/// для человека (ТБ-042). История для модели — только сообщения в пределах ТЕКУЩЕГО допуска.</para>
/// <para>АУДИТ (ТБ-030): в режиме по материалам до обращения к модели — запись <see cref="AuditAction.Search"/> с
/// областью, диалогом и вопросом; генерацию с фрагментами и ответом пишет ядро (<see cref="AuditAction.Generate"/>).
/// Недоступен журнал — модель не вызывается (fail-closed). Свободную генерацию пишет ядро с грифом 0.</para>
/// </remarks>
/// <param name="AreaKey">Область; <see langword="null"/> — область профиля по умолчанию.</param>
/// <param name="ConversationId">Диалог или <see langword="null"/> — начать новый.</param>
/// <param name="Text">Вопрос.</param>
/// <param name="Mode">Режим: по материалам или свободный.</param>
public sealed record SendChatMessageCommand(string? AreaKey, int? ConversationId, string Text, ChatMode Mode = ChatMode.Grounded)
    : IStreamRequest<ChatUpdate>
{
    /// <inheritdoc cref="SendChatMessageCommand" />
    public sealed class Handler(
        IChatProfile profile,
        IConversationStore conversations,
        IGroundedGenerator generator,
        IConversationalGenerator freeGenerator,
        IAuditWriter auditWriter,
        IAccessContextProvider accessProvider) : IStreamRequestHandler<SendChatMessageCommand, ChatUpdate>
    {
        /// <inheritdoc />
        public async IAsyncEnumerable<ChatUpdate> Handle(
            SendChatMessageCommand command, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var text = ChatGate.Question(command.Text);
            if (text.Length == 0)
            {
                yield return new ChatUpdate(Error: ChatMessages.EmptyQuestion);
                yield break;
            }

            var settings = profile.Settings;
            if (command.Mode == ChatMode.Free && !settings.AllowFreeMode)
            {
                yield return new ChatUpdate(Error: ChatMessages.FreeModeOff);
                yield break;
            }

            var entry = await ChatGate.EnterAsync(profile, accessProvider, command.AreaKey, cancellationToken);
            if (entry is not { Area: { } area, Access: { } access })
            {
                yield return new ChatUpdate(Error: entry.Error ?? ChatMessages.NotFound);
                yield break;
            }

            if (entry.SubjectId is not { } me)
            {
                yield return new ChatUpdate(Error: ChatMessages.NoSubject);
                yield break;
            }

            // Материалы области — до диалога: «читать нечего» не должно заводить пустых диалогов.
            ChatScope? scope = null;
            if (command.Mode == ChatMode.Grounded)
            {
                var unavailable = false;
                try
                {
                    scope = await profile.ResolveScopeAsync(area, access, forQuestion: true, cancellationToken);
                }
                catch (ModelUnavailableException)
                {
                    unavailable = true;
                }

                if (unavailable || scope is null)
                {
                    yield return new ChatUpdate(Error: ChatMessages.ModelUnavailable);
                    yield break;
                }

                if (scope.Unavailable is { } reason)
                {
                    yield return new ChatUpdate(Error: reason);
                    yield break;
                }

                if (scope.DocumentIds is { Count: 0 })
                {
                    yield return new ChatUpdate(Error: ChatMessages.NoMaterials);
                    yield break;
                }
            }

            // Диалог: новый (заголовок — начало вопроса) или свой диалог ЭТОЙ области.
            IReadOnlyList<ChatTurn> history = [];
            int conversationId;
            if (command.ConversationId is { } existing)
            {
                var thread = await conversations.GetAsync(existing, me, access.MaxClassification, cancellationToken);
                if (thread is null || !ChatGate.SameArea(thread.Area, area.Key))
                {
                    yield return new ChatUpdate(Error: ChatMessages.NotFound);
                    yield break;
                }

                conversationId = thread.Id;
                history = History(thread.Messages, settings);
            }
            else
            {
                conversationId = await conversations.CreateAsync(
                    me, ChatGate.Title(text), area.Key, area.Classification, area.ScopeKeys, cancellationToken);
            }

            // Вопрос — с грифом области (дело — гриф дела; общая область — без грифа: это слова сотрудника).
            if (!await conversations.AppendMessageAsync(
                    conversationId, me, new ConversationMessageDraft(ConversationMessageRole.User, text, area.Classification), cancellationToken))
            {
                yield return new ChatUpdate(Error: ChatMessages.NotFound);
                yield break;
            }

            if (scope is null)
            {
                await foreach (var update in FreeAsync(conversationId, me, text, history, access, cancellationToken))
                {
                    yield return update;
                }

                yield break;
            }

            // АУДИТ ДО МОДЕЛИ (ТБ-030, fail-closed): нет записи — нет обращения к материалам.
            var inv = CultureInfo.InvariantCulture;
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Search,
                    area.AuditClassification,
                    SubjectId: me,
                    ObjectRef: area.AuditRef + ":chat:" + conversationId.ToString(inv),
                    DivisionId: area.DivisionId,
                    PayloadSensitive: $"Вопрос к материалам {area.Description} ({Materials(scope)}): {text}"),
                cancellationToken);

            yield return new ChatUpdate(ConversationId: conversationId);

            var request = new GroundedRequest(
                text,
                ModelRole.Draft,
                ChatGate.Clamp(settings.TopK, ChatLimits.MaxTopK),
                profile.TaskPrompt(area),
                history,
                scope.DocumentIds is null ? null : new RetrievalFilter(DocumentIds: scope.DocumentIds));

            await using var stream = generator.GenerateStreamingAsync(request, access, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                GroundedStreamUpdate? update = null;
                var failed = false;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        break;
                    }

                    update = stream.Current;
                }
                catch (ModelUnavailableException)
                {
                    // Попытку с уже извлечёнными фрагментами ядро записало в журнал; вопрос остался в диалоге.
                    failed = true;
                }

                if (failed || update is null)
                {
                    yield return new ChatUpdate(ConversationId: conversationId, Error: ChatMessages.ModelUnavailable + " Вопрос сохранён в диалоге.");
                    yield break;
                }

                if (update.Final is { } final)
                {
                    var answer = await SaveAnswerAsync(conversationId, me, area, scope, final, cancellationToken);
                    yield return new ChatUpdate(ConversationId: conversationId, Final: answer);
                    yield break;
                }

                if (!string.IsNullOrEmpty(update.TextDelta))
                {
                    yield return new ChatUpdate(TextDelta: update.TextDelta);
                }
            }
        }

        // Свободный режим: ассистент ядра без поиска и грунтовки (генерацию с грифом 0 пишет в журнал ядро).
        private async IAsyncEnumerable<ChatUpdate> FreeAsync(
            int conversationId, int me, string text, IReadOnlyList<ChatTurn> history, AccessContext access,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatUpdate(ConversationId: conversationId);

            var assembled = new StringBuilder();
            await using var stream = freeGenerator
                .GenerateStreamingAsync(text, history, access, ModelRole.Draft, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                string? delta = null;
                var failed = false;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        break;
                    }

                    delta = stream.Current;
                }
                catch (ModelUnavailableException)
                {
                    failed = true;
                }

                if (failed)
                {
                    yield return new ChatUpdate(ConversationId: conversationId, Error: ChatMessages.ModelUnavailable + " Вопрос сохранён в диалоге.");
                    yield break;
                }

                if (!string.IsNullOrEmpty(delta))
                {
                    assembled.Append(delta);
                    yield return new ChatUpdate(TextDelta: delta);
                }
            }

            var answer = assembled.ToString();
            await conversations.AppendMessageAsync(
                conversationId, me, new ConversationMessageDraft(ConversationMessageRole.Assistant, Clip(answer), 0), cancellationToken);
            yield return new ChatUpdate(ConversationId: conversationId, Final: new ChatAnswer(answer, 0, [], Check: null));
        }

        // Источники — фрагменты в порядке подачи модели (номер = «[Фрагмент N]»); проверка — вердикт ядра + номера.
        private async Task<ChatAnswer> SaveAnswerAsync(
            int conversationId, int me, ChatArea area, ChatScope scope, GroundedResponse final, CancellationToken cancellationToken)
        {
            var sources = final.UsedFragments
                .Select((fragment, index) => ChatSource.From(index + 1, fragment, scope))
                .OfType<ChatSource>()
                .ToList();
            var check = new ChatCheck(
                final.Grounding.Citations.Where(c => c.Status != CitationStatus.Confirmed).Select(c => c.Citation).Distinct().ToList(),
                FragmentReferences.Unknown(final.Answer, final.UsedFragments.Count));
            var classification = (short)Math.Max(final.ResultClassification, area.Classification);
            var answer = new ChatAnswer(final.Answer, classification, sources, check);

            await conversations.AppendMessageAsync(
                conversationId,
                me,
                new ConversationMessageDraft(
                    ConversationMessageRole.Assistant,
                    Clip(final.Answer),
                    classification,
                    ChatJson.Write(final.Grounding),
                    ChatJson.Write(sources),
                    ChatJson.Write(check),
                    sources.Select(s => s.ScopeKey).OfType<string>().Distinct(StringComparer.Ordinal).ToList()),
                cancellationToken);
            return answer;
        }

        // Последние реплики (уже в пределах текущего допуска), каждая — не длиннее предела профиля.
        private static List<ChatTurn> History(IReadOnlyList<ConversationMessage> messages, ChatSettings settings)
        {
            var turns = Math.Clamp(settings.HistoryTurns, 0, ChatLimits.MaxHistoryTurns);
            var length = Math.Max(settings.HistoryTurnLength, 1);
            return messages
                .TakeLast(turns)
                .Select(m => new ChatTurn(m.Role, m.Content.Length <= length ? m.Content : m.Content[..length]))
                .ToList();
        }

        private static string Materials(ChatScope scope) => scope.DocumentIds is { } ids
            ? "материалов в области: " + ids.Count.ToString(CultureInfo.InvariantCulture)
            : "весь доступный корпус";

        private static string Clip(string answer) =>
            answer.Length <= ChatLimits.MaxAnswerLength ? answer : answer[..ChatLimits.MaxAnswerLength] + " […ответ обрезан при сохранении]";
    }
}

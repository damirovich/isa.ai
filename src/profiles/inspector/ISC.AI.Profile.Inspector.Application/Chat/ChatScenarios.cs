using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Chat;

/// <summary>
/// Сценарии чата (UI → ядро): каждый резолвит контекст доступа субъекта через <see cref="IAccessContextProvider"/>
/// и делегирует грунтованному ассистенту (<see cref="IChatService"/>) либо хранилищу диалогов
/// (<see cref="IConversationStore"/>). Режимные инварианты (грунтовка/аудит/доступ) — внутри ядра;
/// разграничение диалогов по владельцу — в хранилище.
/// </summary>

/// <summary>Отправить реплику в чат (новый диалог при <c>ConversationId = null</c>).</summary>
public sealed record SendChatMessageCommand(int? ConversationId, string Text) : IRequest<ResponseDto<ChatReply>>
{
    /// <summary>Резолвит доступ и вызывает грунтованного ассистента (история → генерация → сохранение).</summary>
    public sealed class Handler(IChatService chatService, IAccessContextProvider accessProvider)
        : IRequestHandler<SendChatMessageCommand, ResponseDto<ChatReply>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ChatReply>> Handle(SendChatMessageCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var reply = await chatService.SendAsync(
                new ChatMessageRequest(command.ConversationId, command.Text), access, cancellationToken);
            return ResponseDto<ChatReply>.Ok(reply);
        }
    }
}

/// <summary>Потоковая отправка реплики (печать по кускам). Новый диалог при <c>ConversationId = null</c>.</summary>
public sealed record SendChatMessageStreamCommand(int? ConversationId, string Text, ChatMode Mode)
    : IStreamRequest<ChatStreamUpdate>
{
    /// <summary>Резолвит доступ и прокидывает потоковый ответ ассистента (свободный или грунтованный режим).</summary>
    public sealed class Handler(IChatService chatService, IAccessContextProvider accessProvider)
        : IStreamRequestHandler<SendChatMessageStreamCommand, ChatStreamUpdate>
    {
        /// <inheritdoc />
        public async IAsyncEnumerable<ChatStreamUpdate> Handle(
            SendChatMessageStreamCommand command, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            await foreach (var update in chatService.SendStreamingAsync(
                new ChatMessageRequest(command.ConversationId, command.Text, command.Mode), access, cancellationToken))
            {
                yield return update;
            }
        }
    }
}

/// <summary>Список диалогов текущего пользователя (боковая панель).</summary>
public sealed record ListConversationsQuery : IRequest<ResponseDto<IReadOnlyList<ConversationSummary>>>
{
    /// <inheritdoc cref="ListConversationsQuery" />
    public sealed class Handler(IConversationStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ListConversationsQuery, ResponseDto<IReadOnlyList<ConversationSummary>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ConversationSummary>>> Handle(
            ListConversationsQuery query, CancellationToken cancellationToken)
        {
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } subjectId)
            {
                return ResponseDto<IReadOnlyList<ConversationSummary>>.Ok([]);
            }

            var list = await store.ListAsync(subjectId, cancellationToken);
            return ResponseDto<IReadOnlyList<ConversationSummary>>.Ok(list);
        }
    }
}

/// <summary>История сообщений диалога (для владельца).</summary>
public sealed record GetConversationHistoryQuery(int ConversationId) : IRequest<ResponseDto<IReadOnlyList<ChatTurn>>>
{
    /// <inheritdoc cref="GetConversationHistoryQuery" />
    public sealed class Handler(IConversationStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetConversationHistoryQuery, ResponseDto<IReadOnlyList<ChatTurn>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ChatTurn>>> Handle(
            GetConversationHistoryQuery query, CancellationToken cancellationToken)
        {
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } subjectId)
            {
                return ResponseDto<IReadOnlyList<ChatTurn>>.Ok([]);
            }

            var history = await store.GetHistoryAsync(query.ConversationId, subjectId, cancellationToken);
            return ResponseDto<IReadOnlyList<ChatTurn>>.Ok(history);
        }
    }
}

/// <summary>Переименовать диалог владельца.</summary>
public sealed record RenameConversationCommand(int ConversationId, string Title) : IRequest<ResponseDto<bool>>
{
    /// <inheritdoc cref="RenameConversationCommand" />
    public sealed class Handler(IConversationStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<RenameConversationCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RenameConversationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } subjectId)
            {
                return ResponseDto<bool>.Ok(false);
            }

            var ok = await store.RenameAsync(command.ConversationId, subjectId, command.Title, cancellationToken);
            return ResponseDto<bool>.Ok(ok);
        }
    }
}

/// <summary>Удалить (мягко) диалог владельца.</summary>
public sealed record DeleteConversationCommand(int ConversationId) : IRequest<ResponseDto<bool>>
{
    /// <inheritdoc cref="DeleteConversationCommand" />
    public sealed class Handler(IConversationStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<DeleteConversationCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeleteConversationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } subjectId)
            {
                return ResponseDto<bool>.Ok(false);
            }

            var ok = await store.DeleteAsync(command.ConversationId, subjectId, cancellationToken);
            return ResponseDto<bool>.Ok(ok);
        }
    }
}

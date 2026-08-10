using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Chat;

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


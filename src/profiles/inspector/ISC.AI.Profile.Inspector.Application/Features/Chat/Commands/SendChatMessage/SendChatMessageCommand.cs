using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Chat;

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

